using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;

namespace Ocr.Core.Services;

public sealed class HeuristicFieldRecognizer : IFieldRecognizer
{
    private static readonly Regex DateLikeRegex = new(@"^\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex CurrencyRegex = new(@"^[\$€£]\s?[-+]?\d[\d,]*(\.\d+)?$", RegexOptions.Compiled);

    public FieldRecognitionResult Recognize(IReadOnlyList<PageInfo> pages, double lowFieldThreshold)
    {
        var warnings = new List<IssueInfo>();
        var promotedByPage = new Dictionary<int, int>();

        var candidates = pages
            .SelectMany(page => page.KeyValueCandidates.Select(kv => new CandidatePage(kv, page.PageIndex)))
            .Where(cp => cp.Candidate.Confidence >= 0.68 && !string.IsNullOrWhiteSpace(cp.Candidate.Value.Text))
            .ToList();

        var grouped = candidates
            .GroupBy(c => ToSnakeCase(c.Candidate.Label.Text), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fields = new List<RecognitionFieldInfo>();

        foreach (var group in grouped)
        {
            if (group.Key.Split('_', StringSplitOptions.RemoveEmptyEntries).Length > 4)
            {
                continue;
            }

            var strongest = group
                .OrderByDescending(g => g.Candidate.Confidence)
                .ThenBy(g => g.PageIndex)
                .First();

            if (strongest.Candidate.Value.Text.Contains(':', StringComparison.Ordinal) &&
                !strongest.Candidate.Method.Equals("ocr+regex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (group.Count() > 1)
            {
                warnings.Add(new IssueInfo
                {
                    Code = "competing_field_candidates",
                    Severity = "warning",
                    Message = $"Multiple candidates found for field '{group.Key}'. Strongest candidate retained.",
                    PageIndex = strongest.PageIndex
                });
            }

            var normalized = NormalizeValue(strongest.Candidate.Value.Text, out var normalizationIssue);
            var field = new RecognitionFieldInfo
            {
                FieldId = group.Key,
                Label = strongest.Candidate.Label.Text,
                Type = normalized.Type,
                Value = normalized.Value,
                Normalized = normalized,
                Confidence = strongest.Candidate.Confidence,
                IsLowConfidence = strongest.Candidate.Confidence < lowFieldThreshold,
                Source = new FieldSourceInfo
                {
                    PageIndex = strongest.PageIndex,
                    Bbox = Union([strongest.Candidate.Label.Bbox, strongest.Candidate.Value.Bbox]),
                    TokenIds = strongest.Candidate.Label.TokenIds.Concat(strongest.Candidate.Value.TokenIds).Distinct().ToList(),
                    Method = strongest.Candidate.Method
                },
                Validation = new FieldValidationInfo
                {
                    RulesApplied = ["non_empty_value", "basic_normalization", "confidence_threshold"],
                    Validated = true
                },
                Review = new FieldReviewInfo
                {
                    NeedsReview = false,
                    Reason = null
                }
            };

            if (string.IsNullOrWhiteSpace(strongest.Candidate.Value.Text))
            {
                field.Validation.Validated = false;
                field.Validation.Issues.Add(new IssueInfo
                {
                    Code = "empty_value",
                    Severity = "warning",
                    Message = "Field value is empty.",
                    PageIndex = strongest.PageIndex
                });
            }

            if (!string.IsNullOrWhiteSpace(normalizationIssue))
            {
                field.Validation.Validated = false;
                field.Validation.Issues.Add(new IssueInfo
                {
                    Code = "normalization_issue",
                    Severity = "warning",
                    Message = normalizationIssue,
                    PageIndex = strongest.PageIndex
                });
            }

            if (field.IsLowConfidence)
            {
                field.Review.NeedsReview = true;
                field.Review.Reason = "low_confidence";
                warnings.Add(new IssueInfo
                {
                    Code = "low_confidence_promoted_field",
                    Severity = "warning",
                    Message = $"Field '{field.FieldId}' promoted with low confidence ({field.Confidence:F3}).",
                    PageIndex = strongest.PageIndex
                });
            }

            fields.Add(field);
            promotedByPage[strongest.PageIndex] = promotedByPage.GetValueOrDefault(strongest.PageIndex) + 1;
        }

        return new FieldRecognitionResult
        {
            Fields = fields
                .OrderBy(f => f.Source.PageIndex)
                .ThenBy(f => f.FieldId)
                .ToList(),
            Warnings = warnings,
            PromotedByPage = promotedByPage
        };
    }

    private static TableCellNormalizedInfo NormalizeValue(string text, out string? issue)
    {
        issue = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new TableCellNormalizedInfo
            {
                Type = "string",
                Value = null
            };
        }

        if (CurrencyRegex.IsMatch(trimmed))
        {
            var numeric = trimmed.TrimStart('$', '€', '£').Replace(",", string.Empty);
            if (decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            {
                return new TableCellNormalizedInfo
                {
                    Type = "currency",
                    Value = amount,
                    Currency = trimmed[0].ToString()
                };
            }

            issue = "Value looks currency-like but could not be parsed.";
            return new TableCellNormalizedInfo { Type = "currency", Value = trimmed };
        }

        if (DateLikeRegex.IsMatch(trimmed))
        {
            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
            {
                return new TableCellNormalizedInfo
                {
                    Type = "date",
                    Value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
            }

            issue = "Value looks date-like but parsing failed.";
            return new TableCellNormalizedInfo { Type = "date", Value = trimmed };
        }

        if (decimal.TryParse(trimmed.Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return new TableCellNormalizedInfo
            {
                Type = "number",
                Value = number
            };
        }

        if (trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new TableCellNormalizedInfo
            {
                Type = "boolean",
                Value = true
            };
        }

        if (trimmed.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return new TableCellNormalizedInfo
            {
                Type = "boolean",
                Value = false
            };
        }

        return new TableCellNormalizedInfo
        {
            Type = "string",
            Value = trimmed
        };
    }

    private static string ToSnakeCase(string input)
    {
        var sb = new StringBuilder(input.Length + 8);
        var previousUnderscore = false;

        foreach (var ch in input.Trim().TrimEnd(':'))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                previousUnderscore = false;
            }
            else if (!previousUnderscore)
            {
                sb.Append('_');
                previousUnderscore = true;
            }
        }

        var output = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(output) ? "field" : output;
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

    private sealed record CandidatePage(KeyValueCandidateInfo Candidate, int PageIndex);
}
