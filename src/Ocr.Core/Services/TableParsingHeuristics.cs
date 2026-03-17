using System.Text;
using System.Text.RegularExpressions;

namespace Ocr.Core.Services;

internal static partial class TableParsingHeuristics
{
    private static readonly string[] SummaryKeywords =
    [
        "total",
        "subtotal",
        "grand total",
        "summary",
        "amount due",
        "balance",
        "net",
        "tax",
        "discount"
    ];

    public static string CleanCellText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var parts = WhitespaceRegex().Split(text.Trim())
            .Select(CleanToken)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        var cleaned = EdgeArtifactRegex().Replace(builder.ToString(), string.Empty).Trim();
        return ArtifactOnlyRegex().IsMatch(cleaned) ? string.Empty : cleaned;
    }

    public static int SelectHeaderRowIndex(IReadOnlyList<IReadOnlyList<string>> rowTexts)
    {
        if (rowTexts.Count == 0)
        {
            return 0;
        }

        var candidateCount = Math.Min(4, rowTexts.Count);
        var bestIndex = 0;
        var bestScore = double.NegativeInfinity;

        for (var index = 0; index < candidateCount; index++)
        {
            var score = ScoreHeaderCandidate(rowTexts, index);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestScore >= 0.2 ? bestIndex : 0;
    }

    public static double GetHeaderStrength(IReadOnlyList<IReadOnlyList<string>> rowTexts, int headerRowIndex)
    {
        if (rowTexts.Count == 0 || headerRowIndex < 0 || headerRowIndex >= rowTexts.Count)
        {
            return 0;
        }

        return Math.Clamp(ScoreHeaderCandidate(rowTexts, headerRowIndex), 0, 1);
    }

    public static List<string> BuildColumnNames(IReadOnlyList<IReadOnlyList<string>> rowTexts, int headerRowIndex, int columnCount)
    {
        var names = new List<string>(columnCount);

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var name = GetCellText(rowTexts, headerRowIndex, columnIndex);
            if (IsWeakHeaderText(name))
            {
                name = FindFallbackColumnName(rowTexts, headerRowIndex, columnIndex) ?? $"Column {columnIndex + 1}";
            }

            names.Add(name);
        }

        return names;
    }

    public static string ClassifyRow(IReadOnlyList<IReadOnlyList<string>> rowTexts, int rowIndex, int headerRowIndex)
    {
        if (rowIndex == headerRowIndex)
        {
            return "header";
        }

        var row = rowTexts[rowIndex];
        var stats = AnalyzeRow(row);
        var populatedCounts = rowTexts.Select(candidate => AnalyzeRow(candidate).NonEmptyCount).OrderBy(value => value).ToList();
        var medianPopulated = populatedCounts.Count == 0 ? 0 : populatedCounts[populatedCounts.Count / 2];

        if (IsNoiseRow(stats, row.Count, medianPopulated))
        {
            return "noise";
        }

        if (IsSummaryRow(stats, rowIndex, rowTexts.Count, medianPopulated))
        {
            return "summary";
        }

        return "data";
    }

    private static string? FindFallbackColumnName(IReadOnlyList<IReadOnlyList<string>> rowTexts, int headerRowIndex, int columnIndex)
    {
        for (var rowIndex = 0; rowIndex < rowTexts.Count; rowIndex++)
        {
            if (rowIndex == headerRowIndex)
            {
                continue;
            }

            var candidate = GetCellText(rowTexts, rowIndex, columnIndex);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (candidate.Any(char.IsLetter) && candidate.Count(char.IsDigit) <= Math.Max(1, candidate.Length / 3))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsWeakHeaderText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return !text.Any(char.IsLetter) || ArtifactOnlyRegex().IsMatch(text);
    }

    private static string GetCellText(IReadOnlyList<IReadOnlyList<string>> rowTexts, int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= rowTexts.Count)
        {
            return string.Empty;
        }

        var row = rowTexts[rowIndex];
        return columnIndex >= 0 && columnIndex < row.Count ? row[columnIndex] : string.Empty;
    }

    private static double ScoreHeaderCandidate(IReadOnlyList<IReadOnlyList<string>> rowTexts, int index)
    {
        var row = AnalyzeRow(rowTexts[index]);
        if (row.NonEmptyCount == 0)
        {
            return -1;
        }

        var followingRows = rowTexts
            .Skip(index + 1)
            .Take(3)
            .Select(AnalyzeRow)
            .Where(candidate => candidate.NonEmptyCount > 0)
            .ToList();

        var followingNumericRatio = followingRows.Count == 0 ? 0 : followingRows.Average(candidate => candidate.NumericRatio);
        var topBias = Math.Max(0, 1.0 - (index * 0.2));
        var richTextBonus = row.MaxCellLength >= 4 ? 0.1 : 0;
        var populatedBonus = row.NonEmptyCount >= Math.Max(2, row.ColumnCount / 2) ? 0.1 : 0;
        var summaryPenalty = row.IsProbablySummaryLike ? 0.4 : 0;

        return (row.AlphaRatio * 0.4) +
               (Math.Max(0, followingNumericRatio - row.NumericRatio) * 0.2) +
               (row.UniqueValueRatio * 0.15) +
               (topBias * 0.15) +
               richTextBonus +
               populatedBonus -
               (row.ArtifactRatio * 0.4) -
               summaryPenalty;
    }

    private static bool IsNoiseRow(RowAnalysis row, int columnCount, int medianPopulated)
    {
        if (row.NonEmptyCount == 0)
        {
            return true;
        }

        if (row.AlphaCount == 0 && row.NumericCount == 0)
        {
            return true;
        }

        if (row.NonEmptyCount == 1 && row.JoinedText.Length <= 2)
        {
            return true;
        }

        if (row.NonEmptyCount <= Math.Max(1, columnCount / 4) &&
            row.JoinedText.Length <= 3 &&
            !row.HasSummaryKeyword)
        {
            return true;
        }

        return row.ArtifactRatio >= 0.6 || (medianPopulated > 0 && row.NonEmptyCount == 1 && medianPopulated >= 3 && !row.HasSummaryKeyword);
    }

    private static bool IsSummaryRow(RowAnalysis row, int rowIndex, int totalRowCount, int medianPopulated)
    {
        if (row.IsProbablySummaryLike)
        {
            return true;
        }

        return rowIndex >= Math.Max(1, totalRowCount - 2) &&
               row.NonEmptyCount <= Math.Max(1, medianPopulated - 1) &&
               row.AlphaCount >= 1 &&
               row.NumericCount >= 1;
    }

    private static RowAnalysis AnalyzeRow(IReadOnlyList<string> row)
    {
        var cleaned = row
            .Select(CleanCellText)
            .ToList();

        var nonEmpty = cleaned.Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        if (nonEmpty.Count == 0)
        {
            return new RowAnalysis(row.Count, 0, 0, 0, 1, 0, 0, 0, false, 0, string.Empty);
        }

        var alphaCount = nonEmpty.Count(text => text.Any(char.IsLetter));
        var numericCount = nonEmpty.Count(text => text.Any(char.IsDigit));
        var artifactCount = nonEmpty.Count(text => ArtifactOnlyRegex().IsMatch(text));
        var joinedText = string.Join(" ", nonEmpty);

        return new RowAnalysis(
            row.Count,
            nonEmpty.Count,
            alphaCount,
            numericCount,
            artifactCount,
            (double)alphaCount / nonEmpty.Count,
            (double)numericCount / nonEmpty.Count,
            (double)artifactCount / nonEmpty.Count,
            ContainsSummaryKeyword(joinedText),
            nonEmpty.Max(text => text.Length),
            joinedText)
        {
            UniqueValueRatio = (double)nonEmpty.Distinct(StringComparer.OrdinalIgnoreCase).Count() / nonEmpty.Count,
            IsProbablySummaryLike = IsProbablySummaryLike(nonEmpty, joinedText, row.Count, numericCount)
        };
    }

    private static bool ContainsSummaryKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return SummaryKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsProbablySummaryLike(
        IReadOnlyList<string> nonEmptyCells,
        string joinedText,
        int columnCount,
        int numericCount)
    {
        if (!ContainsSummaryKeyword(joinedText))
        {
            return false;
        }

        var populatedCount = nonEmptyCells.Count;
        var denseHeaderLikeRow = populatedCount >= Math.Max(3, columnCount - 1) && numericCount == 0;
        if (denseHeaderLikeRow)
        {
            return false;
        }

        var firstCell = populatedCount > 0 ? nonEmptyCells[0] : string.Empty;
        if (populatedCount <= 2)
        {
            return true;
        }

        return numericCount > 0 &&
               (populatedCount <= Math.Max(2, columnCount / 2) ||
                firstCell.Contains("total", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Contains("subtotal", StringComparison.OrdinalIgnoreCase) ||
                firstCell.Contains("balance", StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var trimmed = EdgeArtifactRegex().Replace(token.Trim(), string.Empty);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return ArtifactOnlyRegex().IsMatch(trimmed) ? string.Empty : trimmed;
    }

    private sealed class RowAnalysis(
        int columnCount,
        int nonEmptyCount,
        int alphaCount,
        int numericCount,
        int artifactCount,
        double alphaRatio,
        double numericRatio,
        double artifactRatio,
        bool hasSummaryKeyword,
        int maxCellLength,
        string joinedText)
    {
        public int ColumnCount { get; } = columnCount;

        public int NonEmptyCount { get; } = nonEmptyCount;

        public int AlphaCount { get; } = alphaCount;

        public int NumericCount { get; } = numericCount;

        public int ArtifactCount { get; } = artifactCount;

        public double AlphaRatio { get; } = alphaRatio;

        public double NumericRatio { get; } = numericRatio;

        public double ArtifactRatio { get; } = artifactRatio;

        public bool HasSummaryKeyword { get; } = hasSummaryKeyword;

        public int MaxCellLength { get; } = maxCellLength;

        public string JoinedText { get; } = joinedText;

        public double UniqueValueRatio { get; init; }

        public bool IsProbablySummaryLike { get; init; }
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[\s\|\¦_\-–—=~`'""\.,:;\/\\\[\]\(\)]+|[\s\|\¦_\-–—=~`'""\.,:;\/\\\[\]\(\)]+$", RegexOptions.Compiled)]
    private static partial Regex EdgeArtifactRegex();

    [GeneratedRegex(@"^[\s\|\¦_\-–—=~`'""\.,:;\/\\\[\]\(\)]+$", RegexOptions.Compiled)]
    private static partial Regex ArtifactOnlyRegex();
}
