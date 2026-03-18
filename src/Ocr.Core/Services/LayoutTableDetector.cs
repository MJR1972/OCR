using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;
using OpenCvSharp;

namespace Ocr.Core.Services;

public sealed class LayoutTableDetector : ITableDetector
{
    private static readonly Regex DateLikeRegex = new(@"^\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex CurrencyRegex = new(@"^[\$€£]\s?[-+]?\d[\d,]*(\.\d+)?$", RegexOptions.Compiled);

    public TableDetectionResult Detect(PageInfo page, Mat pageImage)
    {
        if (page.Lines.Count < 2 || page.Tokens.Count < 4)
        {
            return new TableDetectionResult();
        }

        var tokenById = page.Tokens.ToDictionary(t => t.Id, t => t);
        var lineRows = page.Lines
            .OrderBy(l => l.Bbox.Y)
            .ThenBy(l => l.Bbox.X)
            .Select(l => BuildLineRow(l, tokenById))
            .Where(r => r.Tokens.Count >= 2)
            .ToList();

        if (lineRows.Count < 2)
        {
            return new TableDetectionResult();
        }

        var groups = BuildCandidateGroups(lineRows);
        var tables = new List<TableInfo>();
        var overlays = new List<TableOverlayInfo>();
        var tableIndex = 1;

        foreach (var group in groups)
        {
            var candidate = BuildTableCandidate(group, page, tableIndex);
            if (candidate is null)
            {
                continue;
            }

            tables.Add(candidate.Table);
            overlays.Add(candidate.Overlay);
            tableIndex++;
        }

        return new TableDetectionResult
        {
            Tables = tables,
            Overlays = overlays
        };
    }

    private static List<List<LineRow>> BuildCandidateGroups(List<LineRow> rows)
    {
        var groups = new List<List<LineRow>>();
        var current = new List<LineRow>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (current.Count == 0)
            {
                current.Add(row);
                continue;
            }

            var previous = current[^1];
            var verticalGap = row.Line.Bbox.Y - (previous.Line.Bbox.Y + previous.Line.Bbox.H);
            var avgHeight = (row.Line.Bbox.H + previous.Line.Bbox.H) / 2.0;
            var maxGap = Math.Max(24, avgHeight * 3.5);

            var similarity = ColumnSignatureSimilarity(previous.ColumnAnchors, row.ColumnAnchors);
            var aligns = similarity >= 0.55 && row.Tokens.Count >= 2;

            if (verticalGap <= maxGap && aligns)
            {
                current.Add(row);
            }
            else
            {
                if (IsPotentialTableGroup(current))
                {
                    groups.Add([.. current]);
                }

                current.Clear();
                current.Add(row);
            }
        }

        if (IsPotentialTableGroup(current))
        {
            groups.Add(current);
        }

        return groups;
    }

    private static bool IsPotentialTableGroup(List<LineRow> rows)
    {
        if (rows.Count < 2)
        {
            return false;
        }

        var avgColumns = rows.Average(r => r.Tokens.Count);
        return avgColumns >= 2.0;
    }

    private static double ColumnSignatureSimilarity(List<int> a, List<int> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var setA = a.ToHashSet();
        var setB = b.ToHashSet();
        var intersect = setA.Count(x => setB.Contains(x));
        var union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static LineRow BuildLineRow(LineInfo line, Dictionary<string, TokenInfo> tokenById)
    {
        var tokens = line.TokenIds
            .Where(tokenById.ContainsKey)
            .Select(id => tokenById[id])
            .OrderBy(t => t.Bbox.X)
            .ThenBy(t => t.Bbox.Y)
            .ToList();

        var anchors = tokens
            .Select(t => (int)Math.Round(t.Bbox.X / 25.0, MidpointRounding.AwayFromZero))
            .Distinct()
            .ToList();

        return new LineRow
        {
            Line = line,
            Tokens = tokens,
            ColumnAnchors = anchors
        };
    }

    private static TableCandidate? BuildTableCandidate(List<LineRow> rows, PageInfo page, int tableIndex)
    {
        var allTokens = rows.SelectMany(r => r.Tokens).ToList();
        if (allTokens.Count < 4)
        {
            return null;
        }

        var colBands = InferColumnBands(allTokens, page.Size.WidthPx);
        if (colBands.Count < 2)
        {
            return null;
        }
        if (colBands.Count > 8)
        {
            return null;
        }

        var rowBands = rows.Select((r, idx) => new TableRowBandInfo
        {
            RowIndex = idx,
            Type = "data",
            Bbox = r.Line.Bbox
        }).ToList();

        var headerColumns = new List<TableHeaderColumnInfo>();
        var headerCells = new List<TableHeaderCellInfo>();
        var dataCells = new List<TableCellInfo>();
        var dataRows = new List<TableRowInfo>();
        var tokenIdsInCells = new HashSet<string>(StringComparer.Ordinal);
        var totalCellTokenAssignments = 0;
        var candidateRows = new List<TableCellCandidateRow>(rows.Count);

        for (var rowIdx = 0; rowIdx < rows.Count; rowIdx++)
        {
            var row = rows[rowIdx];
            var candidates = new List<TableCellCandidate>(colBands.Count);
            for (var c = 0; c < colBands.Count; c++)
            {
                var cellTokens = TokensInColumn(row.Tokens, colBands[c]);
                totalCellTokenAssignments += cellTokens.Count;
                candidates.Add(new TableCellCandidate(
                    rowIdx,
                    c,
                    ComposeCellText(cellTokens),
                    cellTokens.Count == 0 ? 0 : cellTokens.Average(t => t.Confidence),
                    cellTokens.Count == 0 ? Intersect(row.Line.Bbox, colBands[c].Bbox) : Union(cellTokens.Select(t => t.Bbox)),
                    cellTokens.Select(t => t.Id).ToList()));
            }

            candidateRows.Add(new TableCellCandidateRow(rowIdx, row.Line.Bbox, candidates));
        }

        var rowTexts = candidateRows
            .Select(row => (IReadOnlyList<string>)row.Cells.Select(cell => cell.Text).ToList())
            .ToList();
        var headerRowIndex = TableParsingHeuristics.SelectHeaderRowIndex(rowTexts);
        var headerStrength = TableParsingHeuristics.GetHeaderStrength(rowTexts, headerRowIndex);
        var columnNames = TableParsingHeuristics.BuildColumnNames(rowTexts, headerRowIndex, colBands.Count);
        var columnKeys = BuildColumnKeys(columnNames);
        var removedNoiseRows = 0;

        foreach (var row in candidateRows)
        {
            var rowType = TableParsingHeuristics.ClassifyRow(rowTexts, row.RowIndex, headerRowIndex);
            rowBands[row.RowIndex].Type = rowType;

            if (string.Equals(rowType, "noise", StringComparison.Ordinal))
            {
                removedNoiseRows++;
                continue;
            }

            if (string.Equals(rowType, "header", StringComparison.Ordinal))
            {
                for (var c = 0; c < row.Cells.Count; c++)
                {
                    var cell = row.Cells[c];
                    foreach (var tokenId in cell.TokenIds)
                    {
                        tokenIdsInCells.Add(tokenId);
                    }

                    var headerName = string.IsNullOrWhiteSpace(cell.Text) ? columnNames[c] : cell.Text;
                    headerColumns.Add(new TableHeaderColumnInfo
                    {
                        ColIndex = c,
                        Name = headerName,
                        Key = columnKeys[c],
                        Bbox = cell.Bbox,
                        Confidence = cell.Confidence
                    });

                    headerCells.Add(new TableHeaderCellInfo
                    {
                        RowIndex = row.RowIndex,
                        ColIndex = c,
                        RowSpan = 1,
                        ColSpan = 1,
                        Text = headerName,
                        Confidence = cell.Confidence,
                        Bbox = cell.Bbox,
                        TokenIds = [.. cell.TokenIds]
                    });
                }

                continue;
            }

            var rowCellRefs = new List<TableCellRefInfo>();
            var rowValues = new Dictionary<string, object?>();
            var rowConfidences = new List<double>();

            for (var c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                foreach (var tokenId in cell.TokenIds)
                {
                    tokenIdsInCells.Add(tokenId);
                }

                var normalized = NormalizeValue(cell.Text);
                dataCells.Add(new TableCellInfo
                {
                    RowIndex = row.RowIndex,
                    ColIndex = c,
                    RowSpan = 1,
                    ColSpan = 1,
                    Text = cell.Text,
                    Normalized = normalized,
                    Confidence = cell.Confidence,
                    Bbox = cell.Bbox,
                    TokenIds = [.. cell.TokenIds],
                    IsLowConfidence = cell.Confidence < page.Quality.MeanTokenConfidence && cell.Confidence < 0.75
                });

                rowCellRefs.Add(new TableCellRefInfo { RowIndex = row.RowIndex, ColIndex = c });
                rowValues[columnKeys[c]] = normalized.Value;
                if (cell.Confidence > 0)
                {
                    rowConfidences.Add(cell.Confidence);
                }
            }

            dataRows.Add(new TableRowInfo
            {
                RowIndex = row.RowIndex,
                Type = rowType,
                Values = rowValues,
                Source = new TableRowSourceInfo { CellRefs = rowCellRefs },
                Confidence = rowConfidences.Count == 0 ? 0 : rowConfidences.Average(),
                IsLowConfidence = rowConfidences.Count > 0 && rowConfidences.Average() < 0.75
            });
        }

        var tableBbox = Union(rows.Select(r => r.Line.Bbox));
        var totalCells = rows.Count * colBands.Count;
        var averageCellTokenCount = totalCells == 0 ? 0 : (double)totalCellTokenAssignments / totalCells;
        if (averageCellTokenCount < 1.0)
        {
            return null;
        }
        if (dataRows.Count == 0)
        {
            return null;
        }

        var tokenCountInCells = tokenIdsInCells.Count;
        var overlappingTokens = page.Tokens.Count(t => Overlaps(t.Bbox, tableBbox));
        var coverageRatio = overlappingTokens == 0 ? 0 : (double)tokenCountInCells / overlappingTokens;

        var issues = new List<IssueInfo>();
        if (coverageRatio < 0.6)
        {
            issues.Add(new IssueInfo { Code = "low_coverage", Severity = "warning", Message = "Table cell coverage is low." });
        }

        if (headerStrength < 0.45)
        {
            issues.Add(new IssueInfo { Code = "weak_header_detection", Severity = "warning", Message = "Header detection confidence is weak." });
        }

        if (removedNoiseRows > 0)
        {
            issues.Add(new IssueInfo
            {
                Code = "artifact_rows_removed",
                Severity = "info",
                Message = $"Removed {removedNoiseRows} row(s) classified as OCR noise or border artifacts."
            });
        }

        var medianCols = rows.Select(r => r.Tokens.Count).OrderBy(x => x).ElementAt(rows.Count / 2);
        if (Math.Abs(medianCols - colBands.Count) > 1)
        {
            issues.Add(new IssueInfo { Code = "irregular_row_structure", Severity = "warning", Message = "Row structure appears irregular." });
        }

        if (rows.Count < 3)
        {
            issues.Add(new IssueInfo { Code = "sparse_table_candidate", Severity = "warning", Message = "Detected table candidate is sparse." });
        }

        var tableConfidence = Math.Clamp((coverageRatio * 0.55) + (headerStrength < 0.45 ? 0.15 : 0.3) + (Math.Min(1.0, rows.Count / 8.0) * 0.15), 0, 1);

        var table = new TableInfo
        {
            TableId = $"tbl-{tableIndex:0000}",
            Confidence = tableConfidence,
            Bbox = tableBbox,
            Detection = new TableDetectionInfo
            {
                Method = "layout",
                HasExplicitGridLines = false,
                Notes =
                [
                    headerRowIndex == 0 ? "Header row selected from first detected row." : $"Header row selected from detected row {headerRowIndex}.",
                    removedNoiseRows > 0 ? $"Removed {removedNoiseRows} artifact row(s) from structured output." : "No artifact rows removed."
                ]
            },
            Grid = new TableGridInfo
            {
                Rows = rows.Count,
                Cols = colBands.Count,
                RowBands = rowBands,
                ColBands = colBands.Select((b, idx) => new TableColumnBandInfo { ColIndex = idx, Bbox = b.Bbox }).ToList()
            },
            Header = new TableHeaderInfo
            {
                RowIndex = headerRowIndex,
                Columns = headerColumns,
                Cells = headerCells
            },
            Cells = dataCells,
            Rows = dataRows,
            TokenCoverage = new TableTokenCoverageInfo
            {
                TokenCountInCells = tokenCountInCells,
                TokenCountOverlappingTableBbox = overlappingTokens,
                CoverageRatio = coverageRatio
            },
            Issues = issues
        };

        var overlay = new TableOverlayInfo
        {
            PageIndex = page.PageIndex,
            Method = "layout",
            TableBbox = tableBbox,
            RowBands = rowBands.Select(r => r.Bbox).ToList(),
            ColBands = colBands.Select(c => c.Bbox).ToList(),
            Cells = dataCells.Select(c => c.Bbox).ToList()
        };

        return new TableCandidate(table, overlay);
    }

    private static List<ColumnBand> InferColumnBands(List<TokenInfo> tokens, int pageWidth)
    {
        var sorted = tokens.OrderBy(t => t.Bbox.X + t.Bbox.W / 2.0).ToList();
        var bands = new List<ColumnBand>();
        var tolerance = Math.Max(25, (int)(pageWidth * 0.015));

        foreach (var token in sorted)
        {
            var center = token.Bbox.X + token.Bbox.W / 2;
            var matched = bands.FirstOrDefault(b => Math.Abs(b.CenterX - center) <= tolerance);
            if (matched is null)
            {
                bands.Add(new ColumnBand
                {
                    Tokens = [token],
                    CenterX = center
                });
            }
            else
            {
                matched.Tokens.Add(token);
                matched.CenterX = (int)Math.Round(matched.Tokens.Average(t => t.Bbox.X + t.Bbox.W / 2.0), MidpointRounding.AwayFromZero);
            }
        }

        var filtered = bands
            .Where(b => b.Tokens.Count >= 2)
            .OrderBy(b => b.CenterX)
            .ToList();

        return filtered.Select(b => new ColumnBand
        {
            Tokens = b.Tokens,
            CenterX = b.CenterX,
            Bbox = new BboxInfo
            {
                X = b.Tokens.Min(t => t.Bbox.X),
                Y = b.Tokens.Min(t => t.Bbox.Y),
                W = b.Tokens.Max(t => t.Bbox.X + t.Bbox.W) - b.Tokens.Min(t => t.Bbox.X),
                H = b.Tokens.Max(t => t.Bbox.Y + t.Bbox.H) - b.Tokens.Min(t => t.Bbox.Y)
            }
        }).ToList();
    }

    private static List<TokenInfo> TokensInColumn(List<TokenInfo> tokens, ColumnBand band)
    {
        return tokens
            .Where(t => OverlapsHorizontally(t.Bbox, band.Bbox) || Math.Abs((t.Bbox.X + t.Bbox.W / 2) - band.CenterX) <= Math.Max(12, band.Bbox.W / 3))
            .OrderBy(t => t.Bbox.X)
            .ThenBy(t => t.Bbox.Y)
            .ToList();
    }

    private static bool OverlapsHorizontally(BboxInfo a, BboxInfo b)
    {
        var left = Math.Max(a.X, b.X);
        var right = Math.Min(a.X + a.W, b.X + b.W);
        return right > left;
    }

    private static bool Overlaps(BboxInfo a, BboxInfo b)
    {
        var left = Math.Max(a.X, b.X);
        var right = Math.Min(a.X + a.W, b.X + b.W);
        var top = Math.Max(a.Y, b.Y);
        var bottom = Math.Min(a.Y + a.H, b.Y + b.H);
        return right > left && bottom > top;
    }

    private static string ComposeCellText(List<TokenInfo> tokens)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var avgHeight = Math.Max(1, tokens.Average(t => t.Bbox.H));
        var lines = new List<List<TokenInfo>>();

        foreach (var token in tokens.OrderBy(t => t.Bbox.Y).ThenBy(t => t.Bbox.X))
        {
            var line = lines.FirstOrDefault(l => Math.Abs(l.Average(x => x.Bbox.Y) - token.Bbox.Y) <= avgHeight * 0.75);
            if (line is null)
            {
                lines.Add([token]);
            }
            else
            {
                line.Add(token);
            }
        }

        var text = string.Join(
            Environment.NewLine,
            lines.Select(line => string.Join(' ', line.OrderBy(t => t.Bbox.X).Select(t => t.Text))));

        return TableParsingHeuristics.CleanCellTextPreserveLines(text);
    }

    private static TableCellNormalizedInfo NormalizeValue(string text)
    {
        var trimmed = text.Trim();
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
            _ = decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var value);
            return new TableCellNormalizedInfo
            {
                Type = "currency",
                Value = value,
                Currency = trimmed[0].ToString()
            };
        }

        if (DateLikeRegex.IsMatch(trimmed))
        {
            return new TableCellNormalizedInfo
            {
                Type = "date",
                Value = trimmed
            };
        }

        if (decimal.TryParse(trimmed.Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return new TableCellNormalizedInfo
            {
                Type = "number",
                Value = number
            };
        }

        return new TableCellNormalizedInfo
        {
            Type = "string",
            Value = trimmed
        };
    }

    private static bool IsHeaderLikely(LineRow header, List<LineRow> followingRows)
    {
        var headerAlphaRatio = AlphaRatio(header.Tokens);
        var headerNumericRatio = NumericRatio(header.Tokens);

        if (followingRows.Count == 0)
        {
            return headerAlphaRatio >= 0.4;
        }

        var followingNumericRatio = followingRows.Average(r => NumericRatio(r.Tokens));
        return headerAlphaRatio >= headerNumericRatio && followingNumericRatio >= headerNumericRatio;
    }

    private static double AlphaRatio(List<TokenInfo> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        var alpha = tokens.Count(t => t.Text.Any(char.IsLetter));
        return (double)alpha / tokens.Count;
    }

    private static double NumericRatio(List<TokenInfo> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        var numeric = tokens.Count(t => t.Text.Any(char.IsDigit));
        return (double)numeric / tokens.Count;
    }

    private static List<string> BuildColumnKeys(IReadOnlyList<string> columnNames)
    {
        var keys = new List<string>(columnNames.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var c = 0; c < columnNames.Count; c++)
        {
            var baseKey = ToSnakeCase(columnNames[c]);
            if (string.IsNullOrWhiteSpace(baseKey))
            {
                baseKey = $"col_{c + 1}";
            }

            var key = baseKey;
            var suffix = 2;
            while (!used.Add(key))
            {
                key = $"{baseKey}_{suffix}";
                suffix++;
            }

            keys.Add(key);
        }

        return keys;
    }

    private static string ToSnakeCase(string input)
    {
        var sb = new StringBuilder(input.Length + 8);
        var previousUnderscore = false;

        foreach (var ch in input.Trim())
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
        return string.IsNullOrWhiteSpace(output) ? "column" : output;
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

    private static BboxInfo Intersect(BboxInfo row, BboxInfo col)
    {
        var x1 = Math.Max(row.X, col.X);
        var y1 = Math.Max(row.Y, col.Y);
        var x2 = Math.Min(row.X + row.W, col.X + col.W);
        var y2 = Math.Min(row.Y + row.H, col.Y + col.H);

        if (x2 <= x1 || y2 <= y1)
        {
            return new BboxInfo
            {
                X = row.X,
                Y = row.Y,
                W = row.W,
                H = row.H
            };
        }

        return new BboxInfo
        {
            X = x1,
            Y = y1,
            W = x2 - x1,
            H = y2 - y1
        };
    }

    private sealed record TableCellCandidate(
        int RowIndex,
        int ColIndex,
        string Text,
        double Confidence,
        BboxInfo Bbox,
        List<string> TokenIds);

    private sealed record TableCellCandidateRow(
        int RowIndex,
        BboxInfo Bbox,
        List<TableCellCandidate> Cells);

    private sealed class LineRow
    {
        public required LineInfo Line { get; init; }
        public required List<TokenInfo> Tokens { get; init; }
        public required List<int> ColumnAnchors { get; init; }
    }

    private sealed class ColumnBand
    {
        public List<TokenInfo> Tokens { get; init; } = [];
        public int CenterX { get; set; }
        public BboxInfo Bbox { get; init; } = new();
    }

    private sealed record TableCandidate(TableInfo Table, TableOverlayInfo Overlay);
}
