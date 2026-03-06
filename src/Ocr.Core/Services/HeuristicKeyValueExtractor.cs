using System.Text;
using System.Text.RegularExpressions;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;

namespace Ocr.Core.Services;

public sealed class HeuristicKeyValueExtractor : IKeyValueExtractor
{
    private static readonly Regex DateLikeRegex = new(@"^\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex CurrencyLikeRegex = new(@"^[\$€£]\s?[-+]?\d", RegexOptions.Compiled);
    private static readonly Regex LabelCleanRegex = new(@"[^a-zA-Z0-9\s]", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly string[] LabelCueWords =
    [
        "name", "number", "id", "date", "amount", "total", "trace", "policy", "claim", "invoice",
        "account", "phone", "email", "dob", "address", "zip", "city", "state"
    ];

    public KeyValueExtractionResult Extract(PageInfo page)
    {
        var tokenById = page.Tokens.ToDictionary(t => t.Id, t => t);
        var tableTokenIds = CollectTableTokenIds(page);

        var orderedLines = page.Lines
            .OrderBy(l => l.Bbox.Y)
            .ThenBy(l => l.Bbox.X)
            .ToList();

        var candidates = new List<RawCandidate>();
        for (var i = 0; i < orderedLines.Count; i++)
        {
            var line = orderedLines[i];
            var lineTokens = line.TokenIds
                .Where(tokenById.ContainsKey)
                .Select(id => tokenById[id])
                .Where(t => !tableTokenIds.Contains(t.Id))
                .OrderBy(t => t.Bbox.X)
                .ToList();

            if (lineTokens.Count < 2)
            {
                continue;
            }

            var lineText = JoinTokens(lineTokens);
            var hasColon = lineTokens.Any(t => t.Text.Contains(':', StringComparison.Ordinal));
            if (IsLikelyParagraph(lineTokens, lineText) && !hasColon)
            {
                continue;
            }

            var colonCandidate = ExtractColonPattern(lineTokens, lineText, page.PageIndex);
            if (colonCandidate is not null)
            {
                candidates.Add(colonCandidate);
            }

            var gapCandidate = ExtractGapPattern(lineTokens, page.PageIndex);
            if (gapCandidate is not null)
            {
                candidates.Add(gapCandidate);
            }

            var inlineCandidate = ExtractInlinePattern(lineTokens, page.PageIndex);
            if (inlineCandidate is not null)
            {
                candidates.Add(inlineCandidate);
            }

            if (i < orderedLines.Count - 1)
            {
                var nextLine = orderedLines[i + 1];
                var nextTokens = nextLine.TokenIds
                    .Where(tokenById.ContainsKey)
                    .Select(id => tokenById[id])
                    .Where(t => !tableTokenIds.Contains(t.Id))
                    .OrderBy(t => t.Bbox.X)
                    .ToList();

                var nextLineCandidate = ExtractNextLinePattern(lineTokens, nextTokens, line, nextLine, page.PageIndex);
                if (nextLineCandidate is not null)
                {
                    candidates.Add(nextLineCandidate);
                }
            }
        }

        var deduped = Deduplicate(candidates, out var ambiguousCount)
            .OrderBy(c => c.Label.Bbox.Y)
            .ThenBy(c => c.Label.Bbox.X)
            .ToList();

        var finalized = new List<KeyValueCandidateInfo>(deduped.Count);
        for (var i = 0; i < deduped.Count; i++)
        {
            var candidate = deduped[i];
            if (!IsCandidateUsable(candidate))
            {
                continue;
            }

            finalized.Add(new KeyValueCandidateInfo
            {
                PairId = $"kv-{page.PageIndex:000}-{finalized.Count + 1:00000}",
                Label = candidate.Label,
                Value = candidate.Value,
                Confidence = candidate.Confidence,
                Method = candidate.Method
            });
        }

        var warnings = new List<IssueInfo>();
        if (ambiguousCount > 0)
        {
            warnings.Add(new IssueInfo
            {
                Code = "ambiguous_label_value_pairing",
                Severity = "warning",
                Message = $"Page {page.PageIndex} had {ambiguousCount} overlapping key-value candidates; strongest retained.",
                PageIndex = page.PageIndex
            });
        }

        if (finalized.Count > 25 && finalized.Count(c => c.Confidence < 0.62) > 10)
        {
            warnings.Add(new IssueInfo
            {
                Code = "high_weak_kv_candidate_count",
                Severity = "warning",
                Message = $"Page {page.PageIndex} produced many weak key-value candidates.",
                PageIndex = page.PageIndex
            });
        }

        return new KeyValueExtractionResult
        {
            Candidates = finalized,
            Diagnostics = new FieldExtractionDiagnosticsInfo
            {
                PageIndex = page.PageIndex,
                CandidateCount = finalized.Count,
                AmbiguousCandidateCount = ambiguousCount,
                PromotedFieldCount = 0
            },
            Warnings = warnings
        };
    }

    private static bool IsCandidateUsable(RawCandidate candidate)
    {
        if (candidate.Confidence < 0.56 || string.IsNullOrWhiteSpace(candidate.Value.Text))
        {
            return false;
        }

        var labelWordCount = CountWords(candidate.Label.Text);
        var valueWordCount = CountWords(candidate.Value.Text);
        if (labelWordCount == 0 || labelWordCount > 3 || valueWordCount > 10)
        {
            return false;
        }

        if (candidate.Label.Text.Any(char.IsDigit) ||
            candidate.Label.Text.Contains("yes", StringComparison.OrdinalIgnoreCase) ||
            candidate.Label.Text.Contains("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsLabelLike(candidate.Label.Text) || !IsValueLike(candidate.Value.Text))
        {
            return false;
        }

        if (!candidate.Method.Equals("ocr+regex", StringComparison.OrdinalIgnoreCase) &&
            candidate.Value.Text.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        if (!candidate.Method.Equals("ocr+regex", StringComparison.OrdinalIgnoreCase))
        {
            if (labelWordCount > 2)
            {
                return false;
            }

            if (!LabelHasFieldCue(candidate.Label.Text))
            {
                return false;
            }

            if (!HasStructuredValue(candidate.Value.Text, candidate.Label.Text))
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<string> CollectTableTokenIds(PageInfo page)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in page.Tables)
        {
            foreach (var headerCell in table.Header.Cells)
            {
                foreach (var id in headerCell.TokenIds)
                {
                    ids.Add(id);
                }
            }

            foreach (var cell in table.Cells)
            {
                foreach (var id in cell.TokenIds)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static RawCandidate? ExtractColonPattern(List<TokenInfo> lineTokens, string lineText, int pageIndex)
    {
        var colonIndex = lineTokens.FindIndex(t => t.Text.Contains(':', StringComparison.Ordinal));
        if (colonIndex <= 0 || colonIndex >= lineTokens.Count - 1)
        {
            return null;
        }

        var labelTokens = lineTokens.Take(colonIndex + 1).ToList();
        var valueTokens = lineTokens.Skip(colonIndex + 1).ToList();
        if (labelTokens.Count > 4 || valueTokens.Count > 10)
        {
            return null;
        }

        var labelText = CleanLabelText(JoinTokens(labelTokens));
        var valueText = JoinTokens(valueTokens);
        if (!IsLabelLike(labelText) || !IsValueLike(valueText))
        {
            return null;
        }

        return BuildCandidate(labelTokens, valueTokens, "ocr+regex", 0.9);
    }

    private static RawCandidate? ExtractGapPattern(List<TokenInfo> lineTokens, int pageIndex)
    {
        if (lineTokens.Count < 3)
        {
            return null;
        }

        var avgWidth = Math.Max(1, lineTokens.Average(t => t.Bbox.W));
        var threshold = Math.Max(34, avgWidth * 1.4);
        var split = -1;
        var maxGap = 0.0;

        for (var i = 0; i < lineTokens.Count - 1; i++)
        {
            var gap = lineTokens[i + 1].Bbox.X - (lineTokens[i].Bbox.X + lineTokens[i].Bbox.W);
            if (gap > threshold && gap > maxGap)
            {
                split = i;
                maxGap = gap;
            }
        }

        if (split <= 0 || split >= lineTokens.Count - 1)
        {
            return null;
        }

        var labelTokens = lineTokens.Take(split + 1).ToList();
        var valueTokens = lineTokens.Skip(split + 1).ToList();
        if (labelTokens.Count > 4 || valueTokens.Count > 10)
        {
            return null;
        }
        var labelText = CleanLabelText(JoinTokens(labelTokens));
        var valueText = JoinTokens(valueTokens);
        if (valueText.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }
        if (!IsLabelLike(labelText) || !IsValueLike(valueText))
        {
            return null;
        }

        var spatialScore = Math.Clamp(0.65 + (maxGap / (maxGap + 100)), 0.65, 0.9);
        return BuildCandidate(labelTokens, valueTokens, "ocr+layout", spatialScore);
    }

    private static RawCandidate? ExtractInlinePattern(List<TokenInfo> lineTokens, int pageIndex)
    {
        if (lineTokens.Count < 2)
        {
            return null;
        }

        var maxSplit = Math.Min(4, lineTokens.Count - 1);
        for (var split = 1; split <= maxSplit; split++)
        {
            var labelTokens = lineTokens.Take(split).ToList();
            var valueTokens = lineTokens.Skip(split).ToList();
            var labelText = CleanLabelText(JoinTokens(labelTokens));
            var valueText = JoinTokens(valueTokens);
            if (labelTokens.Count > 3 || valueTokens.Count > 8 || valueText.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsLabelLike(labelText))
            {
                continue;
            }

            if (!LooksLikeValueStart(valueTokens.First().Text) || !IsValueLike(valueText))
            {
                continue;
            }

            return BuildCandidate(labelTokens, valueTokens, "ocr+layout", 0.72);
        }

        return null;
    }

    private static RawCandidate? ExtractNextLinePattern(
        List<TokenInfo> lineTokens,
        List<TokenInfo> nextTokens,
        LineInfo line,
        LineInfo nextLine,
        int pageIndex)
    {
        if (nextTokens.Count == 0)
        {
            return null;
        }

        var labelText = CleanLabelText(JoinTokens(lineTokens));
        if (!IsLabelLike(labelText) || IsLikelyParagraph(lineTokens, JoinTokens(lineTokens)))
        {
            return null;
        }
        var hasColon = lineTokens.Any(t => t.Text.Contains(':', StringComparison.Ordinal));
        if (!hasColon && lineTokens.Count > 2)
        {
            return null;
        }
        if (lineTokens.Count > 4 || nextTokens.Count > 10)
        {
            return null;
        }

        var valueText = JoinTokens(nextTokens);
        if (!IsValueLike(valueText) || valueText.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var verticalGap = nextLine.Bbox.Y - (line.Bbox.Y + line.Bbox.H);
        if (verticalGap > Math.Max(40, line.Bbox.H * 2.2))
        {
            return null;
        }

        var horizontalOverlap = OverlapWidth(line.Bbox, nextLine.Bbox);
        if (horizontalOverlap <= 0)
        {
            return null;
        }

        return BuildCandidate(lineTokens, nextTokens, "ocr+layout", 0.7);
    }

    private static List<RawCandidate> Deduplicate(List<RawCandidate> candidates, out int ambiguousCount)
    {
        ambiguousCount = 0;
        var ordered = candidates
            .OrderByDescending(CandidateScore)
            .ThenBy(c => c.Label.Bbox.Y)
            .ThenBy(c => c.Label.Bbox.X)
            .ToList();

        var kept = new List<RawCandidate>();
        foreach (var candidate in ordered)
        {
            var duplicate = kept.Any(existing =>
                string.Equals(NormalizeToken(existing.Label.Text), NormalizeToken(candidate.Label.Text), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeToken(existing.Value.Text), NormalizeToken(candidate.Value.Text), StringComparison.OrdinalIgnoreCase) &&
                IoU(existing.Value.Bbox, candidate.Value.Bbox) >= 0.5);
            var overlapping = kept.Any(existing =>
                TokenOverlap(existing.Value.TokenIds, candidate.Value.TokenIds) >= 0.8 &&
                TokenOverlap(existing.Label.TokenIds, candidate.Label.TokenIds) >= 0.5);
            var labelSupersetOverlap = kept.Any(existing =>
                TokenOverlap(existing.Value.TokenIds, candidate.Value.TokenIds) >= 0.8 &&
                (TokenOverlap(existing.Label.TokenIds, candidate.Label.TokenIds) >= 0.25 ||
                 TokenOverlap(candidate.Label.TokenIds, existing.Label.TokenIds) >= 0.25));

            if (duplicate || overlapping || labelSupersetOverlap)
            {
                ambiguousCount++;
                continue;
            }

            kept.Add(candidate);
        }

        return kept;
    }

    private static RawCandidate BuildCandidate(List<TokenInfo> labelTokens, List<TokenInfo> valueTokens, string method, double spatialScore)
    {
        var label = BuildPart(labelTokens, true);
        var value = BuildPart(valueTokens, false);

        var confidence = Math.Clamp(
            (label.Confidence * 0.35) +
            (value.Confidence * 0.45) +
            (spatialScore * 0.2),
            0,
            1);

        return new RawCandidate(label, value, confidence, method);
    }

    private static KeyValuePartInfo BuildPart(List<TokenInfo> tokens, bool isLabel)
    {
        var text = JoinTokens(tokens);
        if (isLabel)
        {
            text = CleanLabelText(text);
        }

        return new KeyValuePartInfo
        {
            Text = text,
            Confidence = tokens.Count == 0 ? 0 : tokens.Average(t => t.Confidence),
            Bbox = Union(tokens.Select(t => t.Bbox)),
            TokenIds = tokens.Select(t => t.Id).ToList()
        };
    }

    private static string JoinTokens(List<TokenInfo> tokens)
        => string.Join(' ', tokens.Select(t => t.Text)).Trim();

    private static string CleanLabelText(string text)
    {
        var trimmed = text.Trim().TrimEnd(':').Trim();
        return LabelCleanRegex.Replace(trimmed, " ").Replace("  ", " ").Trim();
    }

    private static bool IsLabelLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Length > 42 || text.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 4)
        {
            return false;
        }

        var alphaCount = text.Count(char.IsLetter);
        return alphaCount >= Math.Max(2, text.Length / 3);
    }

    private static bool IsValueLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Length > 96)
        {
            return false;
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 12)
        {
            return false;
        }

        if (DateLikeRegex.IsMatch(text) || CurrencyLikeRegex.IsMatch(text))
        {
            return true;
        }

        if (text.Any(char.IsDigit) || text.Length <= 40)
        {
            return true;
        }

        return !text.EndsWith(".", StringComparison.Ordinal) || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;
    }

    private static bool LooksLikeValueStart(string text)
    {
        var t = text.Trim();
        if (string.IsNullOrWhiteSpace(t))
        {
            return false;
        }

        return t.Any(char.IsDigit) || char.IsUpper(t[0]) || DateLikeRegex.IsMatch(t) || CurrencyLikeRegex.IsMatch(t);
    }

    private static bool IsLikelyParagraph(List<TokenInfo> tokens, string text)
    {
        if (tokens.Count >= 10)
        {
            return true;
        }

        if (text.Length > 85 && text.Contains(' ', StringComparison.Ordinal) && text.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static int OverlapWidth(BboxInfo a, BboxInfo b)
    {
        var left = Math.Max(a.X, b.X);
        var right = Math.Min(a.X + a.W, b.X + b.W);
        return Math.Max(0, right - left);
    }

    private static BboxInfo Union(IEnumerable<BboxInfo> boxes)
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

        return new BboxInfo
        {
            X = minX,
            Y = minY,
            W = Math.Max(0, maxX - minX),
            H = Math.Max(0, maxY - minY)
        };
    }

    private static double IoU(BboxInfo a, BboxInfo b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        if (x2 <= x1 || y2 <= y1)
        {
            return 0;
        }

        var intersection = (x2 - x1) * (y2 - y1);
        var aArea = Math.Max(1, a.W * a.H);
        var bArea = Math.Max(1, b.W * b.H);
        return (double)intersection / (aArea + bArea - intersection);
    }

    private static string NormalizeToken(string text)
        => Regex.Replace(text ?? string.Empty, "\\s+", " ").Trim().ToLowerInvariant();

    private static double TokenOverlap(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var setA = a.ToHashSet(StringComparer.Ordinal);
        var setB = b.ToHashSet(StringComparer.Ordinal);
        var intersect = setA.Count(setB.Contains);
        var min = Math.Min(setA.Count, setB.Count);
        return min == 0 ? 0 : (double)intersect / min;
    }

    private static double CandidateScore(RawCandidate candidate)
    {
        var labelWords = CountWords(candidate.Label.Text);
        var valueWords = CountWords(candidate.Value.Text);
        return candidate.Confidence - (labelWords * 0.04) - (valueWords * 0.01);
    }

    private static int CountWords(string text) => WordRegex.Matches(text ?? string.Empty).Count;

    private static bool HasStructuredValue(string text, string labelText)
    {
        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateLikeRegex.IsMatch(value) || CurrencyLikeRegex.IsMatch(value))
        {
            return true;
        }

        if (value.Any(char.IsDigit))
        {
            return true;
        }

        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // short alphanumeric IDs like ABC123 or X1Y2Z3
        if (value.Length <= 20 && value.Count(char.IsLetterOrDigit) >= Math.Max(5, value.Length - 2))
        {
            return true;
        }

        var normalizedLabel = NormalizeToken(labelText);
        var textValueLabels = new[] { "name", "address", "city", "state", "email" };
        if (textValueLabels.Any(cue => normalizedLabel.Contains(cue, StringComparison.OrdinalIgnoreCase)))
        {
            var words = CountWords(value);
            if (words > 0 && words <= 6 && !value.Contains(':', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LabelHasFieldCue(string labelText)
    {
        var normalized = NormalizeToken(labelText);
        return LabelCueWords.Any(cue => normalized.Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record RawCandidate(KeyValuePartInfo Label, KeyValuePartInfo Value, double Confidence, string Method);
}
