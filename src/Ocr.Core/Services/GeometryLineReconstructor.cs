using System.Text.RegularExpressions;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;

namespace Ocr.Core.Services;

public sealed class GeometryLineReconstructor : ILineReconstructor
{
    private static readonly Regex CollapseSpacesRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly HashSet<char> ClosingPunctuation = ['.', ',', ':', ';', '?', '!', ')', ']', '}'];
    private static readonly HashSet<char> OpeningPunctuation = ['(', '[', '{'];
    private static readonly HashSet<string> ControlLikeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "o", "0", "□", "☐", "◯", "●", "•", "x", "v"
    };

    public LineReconstructionResult Reconstruct(
        IReadOnlyList<TokenInfo> tokens,
        int pageIndex,
        int pageWidth,
        int pageHeight,
        double lowLineThreshold,
        IReadOnlySet<string>? skipTokenIds = null,
        IReadOnlyDictionary<string, string>? reconstructedTextOverrides = null)
    {
        if (tokens.Count == 0)
        {
            return new LineReconstructionResult
            {
                Successful = true
            };
        }

        var sorted = tokens
            .OrderBy(t => CenterY(t.Bbox))
            .ThenBy(t => t.Bbox.X)
            .ThenBy(t => t.Bbox.Y)
            .ToList();

        var clusters = new List<LineCluster>();
        foreach (var token in sorted)
        {
            var cluster = FindBestCluster(token, clusters);
            if (cluster is null)
            {
                clusters.Add(new LineCluster(token));
            }
            else
            {
                cluster.Add(token);
            }
        }

        clusters = clusters
            .OrderBy(c => c.TopY)
            .ThenBy(c => c.LeftX)
            .ToList();

        var lines = new List<LineInfo>(clusters.Count);
        var lineTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokensAssigned = 0;

        for (var i = 0; i < clusters.Count; i++)
        {
            var lineId = $"ln-{pageIndex:000}-{i + 1:00000}";
            var lineTokens = clusters[i].Tokens
                .OrderBy(t => t.Bbox.X)
                .ThenBy(t => t.Bbox.Y)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToList();

            foreach (var token in lineTokens)
            {
                token.LineId = lineId;
            }

            var bbox = Union(lineTokens.Select(t => t.Bbox), pageWidth, pageHeight);
            var confidence = lineTokens.Count == 0 ? 0 : lineTokens.Average(t => t.Confidence);
            var tokenIds = lineTokens.Select(t => t.Id).ToList();
            tokensAssigned += tokenIds.Count;

            lines.Add(new LineInfo
            {
                LineId = lineId,
                Bbox = bbox,
                TokenIds = tokenIds,
                Confidence = confidence,
                IsLowConfidence = confidence < lowLineThreshold
            });

            var lineText = BuildLineText(lineTokens, skipTokenIds, reconstructedTextOverrides);
            lineTexts[lineId] = lineText;
        }

        var fullText = string.Join(
            Environment.NewLine,
            lines
                .OrderBy(l => l.Bbox.Y)
                .ThenBy(l => l.Bbox.X)
                .Select(l => lineTexts.GetValueOrDefault(l.LineId, string.Empty))
                .Where(t => !string.IsNullOrWhiteSpace(t)));

        return new LineReconstructionResult
        {
            Lines = lines,
            LineTexts = lineTexts,
            FullText = fullText,
            TokensAssigned = tokensAssigned,
            Successful = true
        };
    }

    private static LineCluster? FindBestCluster(TokenInfo token, List<LineCluster> clusters)
    {
        var best = default(LineCluster);
        var bestScore = double.MinValue;

        foreach (var cluster in clusters)
        {
            var overlap = VerticalOverlapRatio(token.Bbox, cluster.BoundingBox);
            var centerDelta = Math.Abs(CenterY(token.Bbox) - cluster.CenterY);
            var threshold = Math.Max(3, Math.Min(token.Bbox.H, cluster.AverageHeight) * 0.65);

            if (overlap < 0.25 && centerDelta > threshold)
            {
                continue;
            }

            var score = (overlap * 0.7) - (centerDelta / Math.Max(1, threshold) * 0.3);
            if (score > bestScore)
            {
                bestScore = score;
                best = cluster;
            }
        }

        return best;
    }

    private static string BuildLineText(
        List<TokenInfo> tokens,
        IReadOnlySet<string>? skipTokenIds,
        IReadOnlyDictionary<string, string>? reconstructedTextOverrides)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<(TokenInfo token, string text)>(tokens.Count);
        foreach (var token in tokens)
        {
            if (skipTokenIds is not null && skipTokenIds.Contains(token.Id))
            {
                continue;
            }

            var rawOrOverride = reconstructedTextOverrides is not null &&
                                reconstructedTextOverrides.TryGetValue(token.Id, out var overrideText)
                ? overrideText
                : token.Text;
            var cleaned = CleanupTokenForLine(token, rawOrOverride);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                parts.Add((token, cleaned));
            }
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var text = parts[0].text;
        for (var i = 1; i < parts.Count; i++)
        {
            var prev = parts[i - 1];
            var current = parts[i];
            if (ShouldInsertSpace(prev.token, prev.text, current.token, current.text))
            {
                text += " ";
            }

            text += current.text;
        }

        return CollapseSpacesRegex.Replace(text.Trim(), " ");
    }

    private static string CleanupTokenForLine(TokenInfo token, string? sourceText)
    {
        var value = sourceText?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (IsLikelyControlArtifact(token, value))
        {
            return string.Empty;
        }

        if (value.All(ch => ch == '_' || ch == '—' || ch == '-'))
        {
            return string.Empty;
        }

        value = value.TrimStart('_').TrimEnd('_');
        value = CollapseSpacesRegex.Replace(value, " ");
        return value;
    }

    private static bool ShouldInsertSpace(TokenInfo previous, string previousText, TokenInfo current, string currentText)
    {
        if (previousText.Length == 0 || currentText.Length == 0)
        {
            return false;
        }

        var firstCurrent = currentText[0];
        var lastPrev = previousText[^1];
        if (ClosingPunctuation.Contains(firstCurrent))
        {
            return false;
        }

        if (OpeningPunctuation.Contains(lastPrev))
        {
            return false;
        }

        var gap = current.Bbox.X - (previous.Bbox.X + previous.Bbox.W);
        if (gap <= 0)
        {
            return false;
        }

        var dynamicThreshold = Math.Max(1.0, Math.Max(previous.Bbox.H, current.Bbox.H) * 0.35);
        var relaxedThreshold = Math.Max(1.0, Math.Min(previous.Bbox.W, current.Bbox.W) * 0.08);
        if (char.IsLetterOrDigit(lastPrev) && char.IsLetterOrDigit(firstCurrent))
        {
            return gap >= dynamicThreshold || gap >= relaxedThreshold;
        }

        return gap >= dynamicThreshold || gap >= relaxedThreshold;
    }

    private static bool IsLikelyControlArtifact(TokenInfo token, string value)
    {
        if (value.Length > 2)
        {
            return false;
        }

        var area = token.Bbox.W * token.Bbox.H;
        if (area > 220 || token.Bbox.W < 6 || token.Bbox.H < 6)
        {
            return false;
        }

        var aspect = token.Bbox.H == 0 ? 0 : token.Bbox.W / (double)token.Bbox.H;
        if (aspect < 0.6 || aspect > 1.6)
        {
            return false;
        }

        if (ControlLikeTokens.Contains(value))
        {
            return true;
        }

        return value.Length == 1 && char.IsPunctuation(value[0]);
    }

    private static double CenterY(BboxInfo bbox)
    {
        return bbox.Y + (bbox.H / 2.0);
    }

    private static double VerticalOverlapRatio(BboxInfo a, BboxInfo b)
    {
        var top = Math.Max(a.Y, b.Y);
        var bottom = Math.Min(a.Y + a.H, b.Y + b.H);
        var overlap = Math.Max(0, bottom - top);
        var minHeight = Math.Max(1, Math.Min(a.H, b.H));
        return overlap / (double)minHeight;
    }

    private static BboxInfo Union(IEnumerable<BboxInfo> boxes, int pageWidth, int pageHeight)
    {
        var list = boxes.ToList();
        if (list.Count == 0)
        {
            return new BboxInfo();
        }

        var minX = list.Min(b => b.X);
        var minY = list.Min(b => b.Y);
        var maxX = list.Max(b => b.X + b.W);
        var maxY = list.Max(b => b.Y + b.H);
        var x = Math.Clamp(minX, 0, Math.Max(0, pageWidth - 1));
        var y = Math.Clamp(minY, 0, Math.Max(0, pageHeight - 1));
        var right = Math.Clamp(maxX, x, pageWidth);
        var bottom = Math.Clamp(maxY, y, pageHeight);

        return new BboxInfo
        {
            X = x,
            Y = y,
            W = Math.Max(0, right - x),
            H = Math.Max(0, bottom - y)
        };
    }

    private sealed class LineCluster
    {
        public LineCluster(TokenInfo first)
        {
            Tokens = [first];
            BoundingBox = first.Bbox;
            TopY = first.Bbox.Y;
            LeftX = first.Bbox.X;
            AverageHeight = Math.Max(1, first.Bbox.H);
            CenterY = first.Bbox.Y + (first.Bbox.H / 2.0);
        }

        public List<TokenInfo> Tokens { get; }
        public BboxInfo BoundingBox { get; private set; }
        public int TopY { get; private set; }
        public int LeftX { get; private set; }
        public int AverageHeight { get; private set; }
        public double CenterY { get; private set; }

        public void Add(TokenInfo token)
        {
            Tokens.Add(token);
            TopY = Math.Min(TopY, token.Bbox.Y);
            LeftX = Math.Min(LeftX, token.Bbox.X);
            BoundingBox = Union(Tokens.Select(t => t.Bbox), int.MaxValue, int.MaxValue);
            AverageHeight = (int)Math.Round(Tokens.Average(t => Math.Max(1, t.Bbox.H)));
            CenterY = Tokens.Average(t => t.Bbox.Y + (t.Bbox.H / 2.0));
        }
    }
}
