using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;

namespace Ocr.Core.Services;

public sealed class StructuredFieldExtractor : IStructuredFieldExtractor
{
    private static readonly Regex DateLikeRegex = new(@"^\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4}$|^\d{1,2}\s+[A-Za-z]{3,12}\s+\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex CurrencyRegex = new(@"^[\$€£]\s?[-+]?\d[\d,]*(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"^[-+]?\d[\d,]*(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex EmailRegex = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);
    private static readonly Regex LabelCleanRegex = new(@"[^A-Za-z0-9\s]", RegexOptions.Compiled);
    private static readonly string[] LabelCueWords =
    [
        "name", "date", "birth", "education", "email", "phone", "license", "state", "address",
        "experience", "gender", "language", "vehicle", "availability", "city", "zip", "country"
    ];

    public StructuredFieldExtractionResult Extract(
        IReadOnlyList<PageInfo> pages,
        IReadOnlyList<RecognitionFieldInfo> existingFields,
        double lowFieldThreshold)
    {
        var existingFieldIds = existingFields
            .Select(f => f.FieldId)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var additionalByPage = new Dictionary<int, List<KeyValueCandidateInfo>>();
        var additionalFields = new List<RecognitionFieldInfo>();
        var warnings = new List<IssueInfo>();
        var kvCount = 0;
        var checkboxDerived = 0;

        foreach (var page in pages.OrderBy(p => p.PageIndex))
        {
            var tokenById = page.Tokens.ToDictionary(t => t.Id, t => t, StringComparer.Ordinal);
            var tableTokenIds = CollectTableTokenIds(page);
            var pageCandidates = new List<KeyValueCandidateInfo>();

            pageCandidates.AddRange(ExtractLineCandidates(page, tokenById, tableTokenIds));
            pageCandidates.AddRange(ExtractRegionCandidates(page, tokenById, tableTokenIds, out var regionFields, out var pageWarnings));
            warnings.AddRange(pageWarnings);
            additionalFields.AddRange(regionFields.Where(f => existingFieldIds.Add(f.FieldId)));
            checkboxDerived += regionFields.Count;

            var dedupedCandidates = DeduplicateCandidates(pageCandidates)
                .OrderBy(c => c.Label.Bbox.Y)
                .ThenBy(c => c.Label.Bbox.X)
                .ThenBy(c => c.Value.Bbox.Y)
                .ThenBy(c => c.Value.Bbox.X)
                .ToList();

            if (dedupedCandidates.Count > 0)
            {
                additionalByPage[page.PageIndex] = dedupedCandidates;
                kvCount += dedupedCandidates.Count;
            }

            var promoted = PromoteCandidates(page.PageIndex, dedupedCandidates, lowFieldThreshold, existingFieldIds);
            additionalFields.AddRange(promoted.Fields);
            warnings.AddRange(promoted.Warnings);
        }

        return new StructuredFieldExtractionResult
        {
            AdditionalKeyValueCandidatesByPage = additionalByPage,
            AdditionalFields = additionalFields
                .OrderBy(f => f.Source.PageIndex)
                .ThenBy(f => f.FieldId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Warnings = warnings,
            KeyValueCandidateCount = kvCount,
            PromotedFieldCount = additionalFields.Count,
            CheckboxDerivedFieldCount = checkboxDerived
        };
    }

    private static List<KeyValueCandidateInfo> ExtractLineCandidates(PageInfo page, Dictionary<string, TokenInfo> tokenById, HashSet<string> tableTokenIds)
    {
        var orderedLines = page.Lines
            .OrderBy(l => l.Bbox.Y)
            .ThenBy(l => l.Bbox.X)
            .ToList();

        var candidates = new List<KeyValueCandidateInfo>();
        var nextPairIndex = 1;
        for (var i = 0; i < orderedLines.Count; i++)
        {
            var line = orderedLines[i];
            var tokens = line.TokenIds
                .Where(tokenById.ContainsKey)
                .Select(id => tokenById[id])
                .Where(t => !tableTokenIds.Contains(t.Id))
                .OrderBy(t => t.Bbox.X)
                .ToList();

            if (tokens.Count < 2)
            {
                continue;
            }

            var labelValue = TryExtractLabelValue(tokens);
            if (labelValue is null)
            {
                if (i >= orderedLines.Count - 1)
                {
                    continue;
                }

                var nextLine = orderedLines[i + 1];
                var nextLineTokens = nextLine.TokenIds
                    .Where(tokenById.ContainsKey)
                    .Select(id => tokenById[id])
                    .Where(t => !tableTokenIds.Contains(t.Id))
                    .OrderBy(t => t.Bbox.X)
                    .ToList();

                labelValue = TryExtractLabelWithNextLineValue(tokens, nextLineTokens, line, nextLine);
            }

            if (labelValue is null)
            {
                continue;
            }

            candidates.Add(new KeyValueCandidateInfo
            {
                PairId = $"kv-{page.PageIndex:000}-{nextPairIndex++:00000}",
                Label = labelValue.Value.Label,
                Value = labelValue.Value.Value,
                Confidence = labelValue.Value.Confidence,
                Method = "ocr+layout"
            });
        }

        return candidates;
    }

    private static (KeyValuePartInfo Label, KeyValuePartInfo Value, double Confidence)? TryExtractLabelValue(List<TokenInfo> lineTokens)
    {
        var text = JoinTokens(lineTokens);
        if (string.IsNullOrWhiteSpace(text) || IsLikelyParagraph(lineTokens, text))
        {
            return null;
        }

        var colonIndex = lineTokens.FindIndex(t => t.Text.Contains(':', StringComparison.Ordinal));
        if (colonIndex >= 0 && colonIndex < lineTokens.Count - 1 && colonIndex <= 4)
        {
            var labelTokens = lineTokens.Take(colonIndex + 1).ToList();
            var valueTokens = lineTokens.Skip(colonIndex + 1).ToList();
            return BuildLabelValue(labelTokens, valueTokens, 0.84);
        }

        // Gap-based split to support "Label     Value" patterns.
        var bestSplit = -1;
        var bestGap = 0.0;
        var avgHeight = Math.Max(1.0, lineTokens.Average(t => t.Bbox.H));
        for (var i = 0; i < lineTokens.Count - 1; i++)
        {
            var gap = lineTokens[i + 1].Bbox.X - (lineTokens[i].Bbox.X + lineTokens[i].Bbox.W);
            if (gap > avgHeight * 1.25 && gap > bestGap)
            {
                bestGap = gap;
                bestSplit = i;
            }
        }

        if (bestSplit > 0 && bestSplit < lineTokens.Count - 1 && bestSplit <= 4)
        {
            var labelTokens = lineTokens.Take(bestSplit + 1).ToList();
            var valueTokens = lineTokens.Skip(bestSplit + 1).ToList();
            return BuildLabelValue(labelTokens, valueTokens, 0.76);
        }

        // Compact "Label Value" where label-like cues are strong.
        var maxLabelTokens = Math.Min(3, lineTokens.Count - 1);
        for (var split = 1; split <= maxLabelTokens; split++)
        {
            var labelTokens = lineTokens.Take(split).ToList();
            var valueTokens = lineTokens.Skip(split).ToList();
            var labelText = CleanLabelText(JoinTokens(labelTokens));
            if (!IsLabelLike(labelText))
            {
                continue;
            }

            if (!HasCueWord(labelText) && !valueTokens.Any(t => t.Text.Any(char.IsDigit)))
            {
                continue;
            }

            return BuildLabelValue(labelTokens, valueTokens, 0.7);
        }

        return null;
    }

    private static (KeyValuePartInfo Label, KeyValuePartInfo Value, double Confidence)? TryExtractLabelWithNextLineValue(
        List<TokenInfo> lineTokens,
        List<TokenInfo> nextLineTokens,
        LineInfo line,
        LineInfo nextLine)
    {
        if (nextLineTokens.Count == 0 || lineTokens.Count > 4 || nextLineTokens.Count > 12)
        {
            return null;
        }

        var labelText = CleanLabelText(JoinTokens(lineTokens));
        if (!IsLabelLike(labelText))
        {
            return null;
        }

        var verticalGap = nextLine.Bbox.Y - (line.Bbox.Y + line.Bbox.H);
        if (verticalGap > Math.Max(50, line.Bbox.H * 2.4))
        {
            return null;
        }

        var candidate = BuildLabelValue(lineTokens, nextLineTokens, 0.68);
        return candidate;
    }

    private static List<KeyValueCandidateInfo> ExtractRegionCandidates(
        PageInfo page,
        Dictionary<string, TokenInfo> tokenById,
        HashSet<string> tableTokenIds,
        out List<RecognitionFieldInfo> regionFields,
        out List<IssueInfo> warnings)
    {
        warnings = [];
        regionFields = [];

        var regionOptions = new List<RegionOption>();
        foreach (var region in page.Regions)
        {
            if (!string.Equals(region.Type, "checkbox", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(region.Type, "radio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var labelTokens = region.LabelTokenIds
                .Where(id => tokenById.TryGetValue(id, out _))
                .Select(id => tokenById[id])
                .Where(t => !tableTokenIds.Contains(t.Id))
                .OrderBy(t => t.Bbox.X)
                .ThenBy(t => t.Bbox.Y)
                .ToList();

            if (labelTokens.Count == 0)
            {
                continue;
            }

            var optionText = JoinTokens(labelTokens);
            if (string.IsNullOrWhiteSpace(optionText))
            {
                continue;
            }

            var groupLabel = InferRegionGroupLabel(page, region, labelTokens, tokenById, tableTokenIds);
            if (string.IsNullOrWhiteSpace(groupLabel))
            {
                warnings.Add(new IssueInfo
                {
                    Code = "ambiguous_region_label_association",
                    Severity = "warning",
                    Message = $"Page {page.PageIndex} region {region.RegionId} had ambiguous group label.",
                    PageIndex = page.PageIndex
                });
                continue;
            }

            regionOptions.Add(new RegionOption(
                GroupLabel: groupLabel,
                OptionLabel: optionText,
                OptionTokenIds: labelTokens.Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList(),
                OptionBbox: Union([region.Bbox, Union(labelTokens.Select(t => t.Bbox))]),
                Confidence: Math.Clamp((region.Confidence * 0.6) + (labelTokens.Average(t => t.Confidence) * 0.4), 0, 1),
                IsChecked: region.Value == true,
                PageIndex: page.PageIndex,
                Method: "ocr+layout"));
        }

        if (regionOptions.Count == 0)
        {
            return [];
        }

        var keyValueCandidates = new List<KeyValueCandidateInfo>();
        var pairIndex = 1;
        var grouped = regionOptions
            .GroupBy(o => ToSnakeCase(o.GroupLabel), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var options = group
                .OrderBy(o => o.OptionBbox.Y)
                .ThenBy(o => o.OptionBbox.X)
                .ToList();
            var checkedOptions = options.Where(o => o.IsChecked).ToList();
            if (checkedOptions.Count == 0)
            {
                continue;
            }

            var groupLabelText = options[0].GroupLabel;
            var valueText = checkedOptions.Count == 1
                ? checkedOptions[0].OptionLabel
                : string.Join(", ", checkedOptions.Select(o => o.OptionLabel));
            var labelTokenIds = options.SelectMany(o => o.OptionTokenIds).Distinct(StringComparer.Ordinal).ToList();
            var valueTokenIds = checkedOptions.SelectMany(o => o.OptionTokenIds).Distinct(StringComparer.Ordinal).ToList();
            var groupBbox = Union(options.Select(o => o.OptionBbox));

            keyValueCandidates.Add(new KeyValueCandidateInfo
            {
                PairId = $"kv-{page.PageIndex:000}-{pairIndex++:00000}",
                Label = new KeyValuePartInfo
                {
                    Text = groupLabelText,
                    Confidence = Math.Clamp(options.Average(o => o.Confidence), 0, 1),
                    Bbox = groupBbox,
                    TokenIds = labelTokenIds
                },
                Value = new KeyValuePartInfo
                {
                    Text = valueText,
                    Confidence = Math.Clamp(checkedOptions.Average(o => o.Confidence), 0, 1),
                    Bbox = Union(checkedOptions.Select(o => o.OptionBbox)),
                    TokenIds = valueTokenIds
                },
                Confidence = Math.Clamp((options.Average(o => o.Confidence) * 0.35) + (checkedOptions.Average(o => o.Confidence) * 0.65), 0, 1),
                Method = "ocr+layout"
            });

            var normalized = NormalizeValue(valueText, out var normalizationIssue);
            var confidence = Math.Clamp((options.Average(o => o.Confidence) * 0.4) + (checkedOptions.Average(o => o.Confidence) * 0.6), 0, 1);
            var field = new RecognitionFieldInfo
            {
                FieldId = group.Key,
                Label = groupLabelText,
                Type = normalized.Type,
                Value = valueText,
                Normalized = normalized,
                Confidence = confidence,
                IsLowConfidence = confidence < 0.78,
                Source = new FieldSourceInfo
                {
                    PageIndex = page.PageIndex,
                    Bbox = groupBbox,
                    TokenIds = valueTokenIds,
                    Method = "ocr+layout"
                },
                Validation = new FieldValidationInfo
                {
                    RulesApplied = ["region_grouping", "checked_option_selection", "basic_normalization"],
                    Validated = true
                },
                Review = new FieldReviewInfo
                {
                    NeedsReview = confidence < 0.78,
                    Reason = confidence < 0.78 ? "low_confidence" : null
                }
            };

            if (!string.IsNullOrWhiteSpace(normalizationIssue))
            {
                field.Validation.Validated = false;
                field.Validation.Issues.Add(new IssueInfo
                {
                    Code = "normalization_issue",
                    Severity = "warning",
                    Message = normalizationIssue,
                    PageIndex = page.PageIndex
                });
            }

            regionFields.Add(field);
        }

        return keyValueCandidates;
    }

    private static string InferRegionGroupLabel(
        PageInfo page,
        Ocr.Core.Contracts.RegionInfo region,
        List<TokenInfo> optionLabelTokens,
        Dictionary<string, TokenInfo> tokenById,
        HashSet<string> tableTokenIds)
    {
        var optionLineIds = optionLabelTokens.Select(t => t.LineId).Distinct(StringComparer.Ordinal).ToList();
        foreach (var lineId in optionLineIds)
        {
            var sameLineTokens = page.Tokens
                .Where(t => string.Equals(t.LineId, lineId, StringComparison.Ordinal) && !tableTokenIds.Contains(t.Id))
                .OrderBy(t => t.Bbox.X)
                .ToList();
            if (sameLineTokens.Count == 0)
            {
                continue;
            }

            var leftTokens = sameLineTokens
                .Where(t => t.Bbox.X + t.Bbox.W <= region.Bbox.X - 2)
                .ToList();
            if (leftTokens.Count > 0)
            {
                var text = CleanLabelText(JoinTokens(leftTokens));
                if (IsLabelLike(text))
                {
                    return text;
                }
            }
        }

        // Fallback to nearest line above containing label cues.
        var optionTop = optionLabelTokens.Min(t => t.Bbox.Y);
        var optionLeft = optionLabelTokens.Min(t => t.Bbox.X);
        var candidateLines = page.Lines
            .Where(l => l.Bbox.Y <= optionTop && optionTop - l.Bbox.Y <= 120)
            .OrderByDescending(l => l.Bbox.Y)
            .ThenBy(l => Math.Abs(l.Bbox.X - optionLeft))
            .Take(4)
            .ToList();

        foreach (var line in candidateLines)
        {
            var tokens = line.TokenIds
                .Where(id => tokenById.ContainsKey(id) && !tableTokenIds.Contains(id))
                .Select(id => tokenById[id])
                .OrderBy(t => t.Bbox.X)
                .ToList();
            if (tokens.Count == 0)
            {
                continue;
            }

            var lineText = CleanLabelText(JoinTokens(tokens));
            if (string.IsNullOrWhiteSpace(lineText))
            {
                continue;
            }

            if (HasCueWord(lineText) || lineText.EndsWith(":", StringComparison.Ordinal))
            {
                return lineText;
            }
        }

        return string.Empty;
    }

    private static List<KeyValueCandidateInfo> DeduplicateCandidates(List<KeyValueCandidateInfo> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Label.Bbox.Y)
            .ThenBy(c => c.Label.Bbox.X)
            .ToList();

        var kept = new List<KeyValueCandidateInfo>();
        foreach (var candidate in ordered)
        {
            var isDuplicate = kept.Any(existing =>
                string.Equals(NormalizeText(existing.Label.Text), NormalizeText(candidate.Label.Text), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeText(existing.Value.Text), NormalizeText(candidate.Value.Text), StringComparison.OrdinalIgnoreCase) &&
                IoU(existing.Value.Bbox, candidate.Value.Bbox) >= 0.5);
            if (!isDuplicate)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    private static (List<RecognitionFieldInfo> Fields, List<IssueInfo> Warnings) PromoteCandidates(
        int pageIndex,
        List<KeyValueCandidateInfo> candidates,
        double lowFieldThreshold,
        HashSet<string> existingFieldIds)
    {
        var fields = new List<RecognitionFieldInfo>();
        var warnings = new List<IssueInfo>();
        var grouped = candidates
            .Where(c => c.Confidence >= 0.67 && !string.IsNullOrWhiteSpace(c.Value.Text))
            .GroupBy(c => ToSnakeCase(c.Label.Text), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            if (existingFieldIds.Contains(group.Key))
            {
                continue;
            }

            var strongest = group
                .OrderByDescending(g => g.Confidence)
                .ThenBy(g => g.Label.Bbox.Y)
                .ThenBy(g => g.Label.Bbox.X)
                .First();

            if (group.Count() > 1)
            {
                warnings.Add(new IssueInfo
                {
                    Code = "competing_field_candidates",
                    Severity = "warning",
                    Message = $"Multiple candidates found for field '{group.Key}'. Strongest retained.",
                    PageIndex = pageIndex
                });
            }

            var normalized = NormalizeValue(strongest.Value.Text, out var normalizationIssue);
            var confidence = Math.Clamp(strongest.Confidence, 0, 1);
            var field = new RecognitionFieldInfo
            {
                FieldId = group.Key,
                Label = strongest.Label.Text,
                Type = normalized.Type,
                Value = strongest.Value.Text,
                Normalized = normalized,
                Confidence = confidence,
                IsLowConfidence = confidence < lowFieldThreshold,
                Source = new FieldSourceInfo
                {
                    PageIndex = pageIndex,
                    Bbox = Union([strongest.Label.Bbox, strongest.Value.Bbox]),
                    TokenIds = strongest.Label.TokenIds.Concat(strongest.Value.TokenIds).Distinct(StringComparer.Ordinal).ToList(),
                    Method = strongest.Method
                },
                Validation = new FieldValidationInfo
                {
                    RulesApplied = ["non_empty_value", "basic_normalization", "spatial_pairing"],
                    Validated = true
                },
                Review = new FieldReviewInfo
                {
                    NeedsReview = false,
                    Reason = null
                }
            };

            if (!string.IsNullOrWhiteSpace(normalizationIssue))
            {
                field.Validation.Validated = false;
                field.Validation.Issues.Add(new IssueInfo
                {
                    Code = "normalization_issue",
                    Severity = "warning",
                    Message = normalizationIssue,
                    PageIndex = pageIndex
                });
            }

            if (field.IsLowConfidence)
            {
                field.Review.NeedsReview = true;
                field.Review.Reason = "low_confidence";
            }

            fields.Add(field);
            existingFieldIds.Add(group.Key);
        }

        return (fields, warnings);
    }

    private static (KeyValuePartInfo Label, KeyValuePartInfo Value, double Confidence)? BuildLabelValue(
        List<TokenInfo> labelTokens,
        List<TokenInfo> valueTokens,
        double spatialConfidence)
    {
        if (labelTokens.Count == 0 || valueTokens.Count == 0)
        {
            return null;
        }

        var labelText = CleanLabelText(JoinTokens(labelTokens));
        var valueText = JoinTokens(valueTokens);
        if (!IsLabelLike(labelText) || string.IsNullOrWhiteSpace(valueText))
        {
            return null;
        }

        var label = new KeyValuePartInfo
        {
            Text = labelText,
            Confidence = labelTokens.Average(t => t.Confidence),
            Bbox = Union(labelTokens.Select(t => t.Bbox)),
            TokenIds = labelTokens.Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList()
        };

        var value = new KeyValuePartInfo
        {
            Text = valueText,
            Confidence = valueTokens.Average(t => t.Confidence),
            Bbox = Union(valueTokens.Select(t => t.Bbox)),
            TokenIds = valueTokens.Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList()
        };

        var confidence = Math.Clamp((label.Confidence * 0.35) + (value.Confidence * 0.45) + (spatialConfidence * 0.2), 0, 1);
        return (label, value, confidence);
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

    private static TableCellNormalizedInfo NormalizeValue(string valueText, out string? normalizationIssue)
    {
        normalizationIssue = null;
        var text = NormalizeText(valueText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TableCellNormalizedInfo
            {
                Type = "string",
                Value = null
            };
        }

        if (CurrencyRegex.IsMatch(text))
        {
            var numeric = text.TrimStart('$', '€', '£').Replace(",", string.Empty, StringComparison.Ordinal);
            if (decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
            {
                return new TableCellNormalizedInfo
                {
                    Type = "currency",
                    Value = amount,
                    Currency = text[0].ToString(CultureInfo.InvariantCulture)
                };
            }

            normalizationIssue = "Value appears to be currency but parsing failed.";
            return new TableCellNormalizedInfo { Type = "currency", Value = text };
        }

        if (DateLikeRegex.IsMatch(text))
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ||
                DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
            {
                return new TableCellNormalizedInfo
                {
                    Type = "date",
                    Value = dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
            }

            normalizationIssue = "Value appears to be date-like but parsing failed.";
            return new TableCellNormalizedInfo { Type = "date", Value = text };
        }

        if (NumberRegex.IsMatch(text) && decimal.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return new TableCellNormalizedInfo
            {
                Type = "number",
                Value = number
            };
        }

        if (text.Equals("yes", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new TableCellNormalizedInfo
            {
                Type = "boolean",
                Value = true
            };
        }

        if (text.Equals("no", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase))
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
            Value = text
        };
    }

    private static bool IsLikelyParagraph(List<TokenInfo> tokens, string text)
    {
        if (tokens.Count >= 10)
        {
            return true;
        }

        return text.Length > 95 && text.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsLabelLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var clean = CleanLabelText(text);
        if (string.IsNullOrWhiteSpace(clean) || clean.Length > 64)
        {
            return false;
        }

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 6)
        {
            return false;
        }

        if (words.Length == 1 && words[0].Length <= 1)
        {
            return false;
        }

        return words.Any(w => w.Any(char.IsLetter));
    }

    private static bool HasCueWord(string labelText)
    {
        var normalized = NormalizeText(labelText);
        return LabelCueWords.Any(cue => normalized.Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private static string JoinTokens(IEnumerable<TokenInfo> tokens)
        => string.Join(' ', tokens.Select(t => (t.Text ?? string.Empty).Trim())).Trim();

    private static string CleanLabelText(string text)
    {
        var cleaned = (text ?? string.Empty).Trim().TrimEnd(':', '*').Trim();
        cleaned = LabelCleanRegex.Replace(cleaned, " ");
        return Regex.Replace(cleaned, "\\s+", " ").Trim();
    }

    private static string NormalizeText(string text)
        => Regex.Replace((text ?? string.Empty).Trim(), "\\s+", " ").Trim();

    private static string ToSnakeCase(string text)
    {
        var cleaned = CleanLabelText(text);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "field";
        }

        var sb = new StringBuilder(cleaned.Length + 8);
        var prevUnderscore = false;
        foreach (var ch in cleaned)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevUnderscore = false;
            }
            else if (!prevUnderscore)
            {
                sb.Append('_');
                prevUnderscore = true;
            }
        }

        var snake = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(snake) ? "field" : snake;
    }

    private static BboxInfo Union(IEnumerable<BboxInfo> boxes)
    {
        var list = boxes.Where(b => b.W > 0 && b.H > 0).ToList();
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

        var inter = (x2 - x1) * (y2 - y1);
        var aArea = Math.Max(1, a.W * a.H);
        var bArea = Math.Max(1, b.W * b.H);
        return (double)inter / (aArea + bArea - inter);
    }

    private sealed record RegionOption(
        string GroupLabel,
        string OptionLabel,
        List<string> OptionTokenIds,
        BboxInfo OptionBbox,
        double Confidence,
        bool IsChecked,
        int PageIndex,
        string Method);
}
