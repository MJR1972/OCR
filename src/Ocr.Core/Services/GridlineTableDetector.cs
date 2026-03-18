using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;
using OpenCvSharp;

namespace Ocr.Core.Services;

public sealed class GridlineTableDetector : ITableDetector
{
    private static readonly Regex DateLikeRegex = new(@"^\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4}$", RegexOptions.Compiled);
    private static readonly Regex CurrencyRegex = new(@"^[\$€£]\s?[-+]?\d[\d,]*(\.\d+)?$", RegexOptions.Compiled);
    private static readonly HashSet<char> ClosingPunctuation = ['.', ',', ':', ';', '?', '!', ')', ']', '}'];
    private static readonly HashSet<char> OpeningPunctuation = ['(', '[', '{'];

    public TableDetectionResult Detect(PageInfo page, Mat pageImage)
    {
        if (page.Tokens.Count < 4 || pageImage.Empty())
        {
            return new TableDetectionResult();
        }

        using var gray = EnsureGray(pageImage);
        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 31, 10);

        using var horizontal = new Mat();
        using var vertical = new Mat();
        using var horizontalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(40, 1));
        using var verticalKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, 40));
        Cv2.MorphologyEx(binary, horizontal, MorphTypes.Open, horizontalKernel);
        Cv2.MorphologyEx(binary, vertical, MorphTypes.Open, verticalKernel);

        using var grid = new Mat();
        Cv2.Add(horizontal, vertical, grid);

        Cv2.FindContours(grid, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var candidateRects = contours
            .Select(Cv2.BoundingRect)
            .Where(r => r.Width >= gray.Width * 0.2 && r.Height >= gray.Height * 0.08)
            .OrderBy(r => r.Y)
            .ThenBy(r => r.X)
            .ToList();

        if (candidateRects.Count == 0)
        {
            return new TableDetectionResult();
        }

        var tables = new List<TableInfo>();
        var overlays = new List<TableOverlayInfo>();
        var tableIndex = 1;

        foreach (var rect in candidateRects)
        {
            var horizontalLines = DetectHorizontalLines(horizontal, rect);
            var verticalLines = DetectVerticalLines(vertical, rect);

            // Valid gridline candidate: required line axes exist and they form rectangular regions.
            if (!IsValidGridlineCandidate(horizontalLines, verticalLines))
            {
                continue;
            }

            var table = BuildTableFromGrid(page, rect, horizontalLines, verticalLines, tableIndex, out var overlay);
            if (table is null)
            {
                continue;
            }

            tables.Add(table);
            overlays.Add(overlay);
            tableIndex++;
        }

        return new TableDetectionResult
        {
            Tables = RemoveNestedDuplicates(tables),
            Overlays = RemoveNestedDuplicateOverlays(tables, overlays)
        };
    }

    private static Mat EnsureGray(Mat source)
    {
        if (source.Channels() == 1)
        {
            return source.Clone();
        }

        var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static bool IsValidGridlineCandidate(List<int> horizontalLines, List<int> verticalLines)
    {
        if (horizontalLines.Count < 2 || verticalLines.Count < 2)
        {
            return false;
        }

        return (horizontalLines.Count - 1) > 0 && (verticalLines.Count - 1) > 0;
    }

    private static List<int> DetectHorizontalLines(Mat horizontal, OpenCvSharp.Rect candidate)
    {
        using var roi = new Mat(horizontal, candidate);
        var yPositions = new List<int>();

        for (var y = 0; y < roi.Rows; y++)
        {
            using var row = roi.Row(y);
            var onPixels = Cv2.CountNonZero(row);
            if (onPixels >= candidate.Width * 0.5)
            {
                yPositions.Add(candidate.Y + y);
            }
        }

        var merged = MergeClosePositions(yPositions, 5);
        return EnsureBoundaryLines(merged, candidate.Y, candidate.Y + candidate.Height);
    }

    private static List<int> DetectVerticalLines(Mat vertical, OpenCvSharp.Rect candidate)
    {
        using var roi = new Mat(vertical, candidate);
        var xPositions = new List<int>();

        for (var x = 0; x < roi.Cols; x++)
        {
            using var col = roi.Col(x);
            var onPixels = Cv2.CountNonZero(col);
            if (onPixels >= candidate.Height * 0.5)
            {
                xPositions.Add(candidate.X + x);
            }
        }

        var merged = MergeClosePositions(xPositions, 5);
        return EnsureBoundaryLines(merged, candidate.X, candidate.X + candidate.Width);
    }

    private static List<int> EnsureBoundaryLines(List<int> lines, int minBoundary, int maxBoundary)
    {
        var result = lines.OrderBy(v => v).ToList();
        if (result.Count == 0)
        {
            return [];
        }

        if (Math.Abs(result[0] - minBoundary) > 8)
        {
            result.Insert(0, minBoundary);
        }

        if (Math.Abs(result[^1] - maxBoundary) > 8)
        {
            result.Add(maxBoundary);
        }

        return result.Distinct().OrderBy(v => v).ToList();
    }

    private static List<int> MergeClosePositions(List<int> positions, int tolerance)
    {
        if (positions.Count == 0)
        {
            return [];
        }

        positions.Sort();
        var merged = new List<int>();
        var bucket = new List<int> { positions[0] };

        for (var i = 1; i < positions.Count; i++)
        {
            if (positions[i] - positions[i - 1] <= tolerance)
            {
                bucket.Add(positions[i]);
            }
            else
            {
                merged.Add((int)Math.Round(bucket.Average(), MidpointRounding.AwayFromZero));
                bucket.Clear();
                bucket.Add(positions[i]);
            }
        }

        merged.Add((int)Math.Round(bucket.Average(), MidpointRounding.AwayFromZero));
        return merged;
    }

    private static TableInfo? BuildTableFromGrid(
        PageInfo page,
        OpenCvSharp.Rect candidateRect,
        List<int> horizontalLines,
        List<int> verticalLines,
        int tableIndex,
        out TableOverlayInfo overlay)
    {
        horizontalLines = horizontalLines.OrderBy(v => v).ToList();
        verticalLines = verticalLines.OrderBy(v => v).ToList();

        candidateRect = ExpandTableUpwardForHeaderTokens(page.Tokens, candidateRect, horizontalLines, verticalLines, page.Size.WidthPx, page.Size.HeightPx);
        horizontalLines = EnsureSyntheticHeaderBoundary(page.Tokens, candidateRect, horizontalLines, verticalLines, page.Size.WidthPx).OrderBy(v => v).ToList();

        var rowBands = BuildRowBands(horizontalLines, candidateRect, page.Size.WidthPx, page.Size.HeightPx);
        var colBands = BuildColumnBands(verticalLines, candidateRect, page.Size.WidthPx, page.Size.HeightPx);

        if (rowBands.Count == 0 || colBands.Count == 0)
        {
            overlay = new TableOverlayInfo();
            return null;
        }

        // Validation rule: reject suspiciously wide pseudo-layout structures.
        if (colBands.Count > 8 && verticalLines.Count <= 2)
        {
            overlay = new TableOverlayInfo();
            return null;
        }

        var cells = BuildCells(rowBands, colBands, page.Size.WidthPx, page.Size.HeightPx);
        if (cells.Count == 0)
        {
            overlay = new TableOverlayInfo();
            return null;
        }

        var candidateRows = new List<TableCellCandidateRow>(rowBands.Count);
        var headerColumns = new List<TableHeaderColumnInfo>();
        var headerCells = new List<TableHeaderCellInfo>();
        var dataCells = new List<TableCellInfo>();
        var dataRows = new List<TableRowInfo>();

        var tokenIdsInCells = new HashSet<string>(StringComparer.Ordinal);
        var totalCellTokenAssignments = 0;
        for (var rowIndex = 0; rowIndex < rowBands.Count; rowIndex++)
        {
            var rowCandidates = new List<TableCellCandidate>(colBands.Count);
            for (var colIndex = 0; colIndex < colBands.Count; colIndex++)
            {
                var cellBox = cells.First(c => c.RowIndex == rowIndex && c.ColIndex == colIndex);
                var cellTokens = AssignTokensToCell(page.Tokens, cellBox.Bbox);
                totalCellTokenAssignments += cellTokens.Count;

                rowCandidates.Add(new TableCellCandidate(
                    rowIndex,
                    colIndex,
                    TableParsingHeuristics.CleanCellTextPreserveLines(ComposeCellText(cellTokens)),
                    cellTokens.Count == 0 ? 0 : cellTokens.Average(t => t.Confidence),
                    cellTokens.Count == 0 ? cellBox.Bbox : Union(cellTokens.Select(t => t.Bbox)),
                    cellTokens.Select(t => t.Id).ToList()));
            }

            candidateRows.Add(new TableCellCandidateRow(rowIndex, rowBands[rowIndex].Bbox, rowCandidates));
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
                for (var colIndex = 0; colIndex < row.Cells.Count; colIndex++)
                {
                    var cell = row.Cells[colIndex];
                    foreach (var tokenId in cell.TokenIds)
                    {
                        tokenIdsInCells.Add(tokenId);
                    }

                    var headerName = string.IsNullOrWhiteSpace(cell.Text) ? columnNames[colIndex] : cell.Text;
                    headerColumns.Add(new TableHeaderColumnInfo
                    {
                        ColIndex = colIndex,
                        Name = headerName,
                        Key = columnKeys[colIndex],
                        Bbox = cell.Bbox,
                        Confidence = cell.Confidence
                    });

                    headerCells.Add(new TableHeaderCellInfo
                    {
                        RowIndex = row.RowIndex,
                        ColIndex = colIndex,
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

            var rowValues = new Dictionary<string, object?>();
            var rowCellRefs = new List<TableCellRefInfo>();
            var rowConfidences = new List<double>();

            for (var colIndex = 0; colIndex < row.Cells.Count; colIndex++)
            {
                var cell = row.Cells[colIndex];
                foreach (var tokenId in cell.TokenIds)
                {
                    tokenIdsInCells.Add(tokenId);
                }

                var normalized = NormalizeValue(cell.Text);
                dataCells.Add(new TableCellInfo
                {
                    RowIndex = row.RowIndex,
                    ColIndex = colIndex,
                    RowSpan = 1,
                    ColSpan = 1,
                    Text = cell.Text,
                    Normalized = normalized,
                    Confidence = cell.Confidence,
                    Bbox = cell.Bbox,
                    TokenIds = [.. cell.TokenIds],
                    IsLowConfidence = cell.Confidence > 0 && cell.Confidence < 0.75
                });

                rowCellRefs.Add(new TableCellRefInfo { RowIndex = row.RowIndex, ColIndex = colIndex });
                rowValues[columnKeys[colIndex]] = normalized.Value;

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

        var averageCellTokenCount = cells.Count == 0 ? 0 : (double)totalCellTokenAssignments / cells.Count;
        if (averageCellTokenCount < 1.0)
        {
            overlay = new TableOverlayInfo();
            return null;
        }

        if (dataRows.Count == 0)
        {
            overlay = new TableOverlayInfo();
            return null;
        }

        var tableBbox = new BboxInfo
        {
            X = candidateRect.X,
            Y = candidateRect.Y,
            W = candidateRect.Width,
            H = candidateRect.Height
        };

        var overlappingTokens = page.Tokens.Where(t => Overlaps(t.Bbox, tableBbox)).ToList();
        var coverageRatio = overlappingTokens.Count == 0 ? 0 : (double)tokenIdsInCells.Count / overlappingTokens.Count;

        var headerAlphaRatio = AlphaRatio(headerCells.Select(c => c.Text).ToList());
        var rowRegularity = Regularity(rowBands.Select(r => r.Bbox.H).ToList());
        var colRegularity = Regularity(colBands.Select(c => c.Bbox.W).ToList());
        var geometryScore = (rowRegularity + colRegularity) / 2.0;
        var confidenceScore = Math.Clamp((coverageRatio * 0.45) + (geometryScore * 0.35) + (headerAlphaRatio * 0.2), 0, 1);

        var issues = new List<IssueInfo>();
        if (coverageRatio < 0.6)
        {
            issues.Add(new IssueInfo { Code = "low_coverage", Severity = "warning", Message = "Table cell coverage is low." });
        }

        if (headerStrength < 0.45 || headerAlphaRatio < 0.45)
        {
            issues.Add(new IssueInfo { Code = "weak_header_detection", Severity = "warning", Message = "Header row is weakly textual." });
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

        if (geometryScore < 0.5)
        {
            issues.Add(new IssueInfo { Code = "weak_gridline_detection", Severity = "warning", Message = "Detected grid geometry is weak." });
        }

        var table = new TableInfo
        {
            TableId = $"tbl-{tableIndex:0000}",
            Confidence = confidenceScore,
            Bbox = tableBbox,
            Detection = new TableDetectionInfo
            {
                Method = "lines",
                HasExplicitGridLines = true,
                Notes =
                [
                    headerRowIndex == 0 ? "Header row selected from first detected row." : $"Header row selected from detected row {headerRowIndex}.",
                    removedNoiseRows > 0 ? $"Removed {removedNoiseRows} artifact row(s) from structured output." : "No artifact rows removed."
                ]
            },
            Grid = new TableGridInfo
            {
                Rows = rowBands.Count,
                Cols = colBands.Count,
                RowBands = rowBands,
                ColBands = colBands
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
                TokenCountInCells = tokenIdsInCells.Count,
                TokenCountOverlappingTableBbox = overlappingTokens.Count,
                CoverageRatio = coverageRatio
            },
            Issues = issues
        };

        overlay = new TableOverlayInfo
        {
            PageIndex = page.PageIndex,
            Method = "lines",
            TableBbox = tableBbox,
            RowBands = rowBands.Select(r => r.Bbox).ToList(),
            ColBands = colBands.Select(c => c.Bbox).ToList(),
            Cells = cells.Select(c => c.Bbox).ToList(),
            HorizontalLinesY = horizontalLines,
            VerticalLinesX = verticalLines
        };

        return table;
    }

    private static OpenCvSharp.Rect ExpandTableUpwardForHeaderTokens(
        IReadOnlyList<TokenInfo> tokens,
        OpenCvSharp.Rect candidateRect,
        IReadOnlyList<int> horizontalLines,
        IReadOnlyList<int> verticalLines,
        int maxWidth,
        int maxHeight)
    {
        var headerTokens = FindPotentialHeaderTokens(tokens, candidateRect, horizontalLines, verticalLines);
        if (headerTokens.Count == 0)
        {
            return candidateRect;
        }

        var minY = Math.Max(0, headerTokens.Min(token => token.Bbox.Y) - 4);
        if (minY >= candidateRect.Y)
        {
            return candidateRect;
        }

        var newHeight = Math.Min(maxHeight - minY, (candidateRect.Y + candidateRect.Height) - minY);
        return new OpenCvSharp.Rect(candidateRect.X, minY, Math.Min(candidateRect.Width, maxWidth - candidateRect.X), Math.Max(candidateRect.Height, newHeight));
    }

    private static List<int> EnsureSyntheticHeaderBoundary(
        IReadOnlyList<TokenInfo> tokens,
        OpenCvSharp.Rect candidateRect,
        IReadOnlyList<int> horizontalLines,
        IReadOnlyList<int> verticalLines,
        int pageWidth)
    {
        if (horizontalLines.Count == 0)
        {
            return [.. horizontalLines];
        }

        var headerTokens = FindPotentialHeaderTokens(tokens, candidateRect, horizontalLines, verticalLines);
        if (headerTokens.Count == 0)
        {
            return [.. horizontalLines];
        }

        var syntheticTop = Math.Max(candidateRect.Y, headerTokens.Min(token => token.Bbox.Y) - 4);
        var firstBoundary = horizontalLines.Min();
        if (firstBoundary - syntheticTop < 10)
        {
            return [.. horizontalLines];
        }

        var updated = new List<int>(horizontalLines.Count + 1) { syntheticTop };
        updated.AddRange(horizontalLines);
        return MergeClosePositions(updated, Math.Max(4, pageWidth / 200));
    }

    private static List<TokenInfo> FindPotentialHeaderTokens(
        IReadOnlyList<TokenInfo> tokens,
        OpenCvSharp.Rect candidateRect,
        IReadOnlyList<int> horizontalLines,
        IReadOnlyList<int> verticalLines)
    {
        if (horizontalLines.Count == 0 || verticalLines.Count < 2)
        {
            return [];
        }

        var firstBoundary = horizontalLines.Min();
        var searchTop = Math.Max(0, firstBoundary - 28);
        var searchBottom = firstBoundary + 2;
        var columnCount = Math.Max(1, verticalLines.Count - 1);

        var candidates = tokens
            .Where(token =>
            {
                var centerX = token.Bbox.X + (token.Bbox.W / 2);
                var centerY = token.Bbox.Y + (token.Bbox.H / 2);
                return centerX >= candidateRect.X &&
                       centerX <= candidateRect.X + candidateRect.Width &&
                       centerY >= searchTop &&
                       centerY <= searchBottom &&
                       token.Text.Any(char.IsLetter);
            })
            .OrderBy(token => token.Bbox.X)
            .ToList();

        if (candidates.Count < Math.Min(3, columnCount))
        {
            return [];
        }

        var coveredColumns = new HashSet<int>();
        foreach (var token in candidates)
        {
            var centerX = token.Bbox.X + (token.Bbox.W / 2);
            for (var index = 0; index < verticalLines.Count - 1; index++)
            {
                if (centerX >= verticalLines[index] && centerX <= verticalLines[index + 1])
                {
                    coveredColumns.Add(index);
                    break;
                }
            }
        }

        return coveredColumns.Count >= Math.Max(3, columnCount - 1) ? candidates : [];
    }

    private static List<TableRowBandInfo> BuildRowBands(List<int> horizontalLines, OpenCvSharp.Rect bounds, int maxW, int maxH)
    {
        var rows = new List<TableRowBandInfo>();
        for (var i = 0; i < horizontalLines.Count - 1; i++)
        {
            var y1 = horizontalLines[i];
            var y2 = horizontalLines[i + 1];
            if (y2 - y1 < 10)
            {
                continue;
            }

            rows.Add(new TableRowBandInfo
            {
                RowIndex = rows.Count,
                Type = rows.Count == 0 ? "header" : "data",
                Bbox = Clip(new BboxInfo { X = bounds.X, Y = y1, W = bounds.Width, H = y2 - y1 }, maxW, maxH)
            });
        }

        return rows;
    }

    private static List<TableColumnBandInfo> BuildColumnBands(List<int> verticalLines, OpenCvSharp.Rect bounds, int maxW, int maxH)
    {
        var cols = new List<TableColumnBandInfo>();
        for (var i = 0; i < verticalLines.Count - 1; i++)
        {
            var x1 = verticalLines[i];
            var x2 = verticalLines[i + 1];
            if (x2 - x1 < 14)
            {
                continue;
            }

            cols.Add(new TableColumnBandInfo
            {
                ColIndex = cols.Count,
                Bbox = Clip(new BboxInfo { X = x1, Y = bounds.Y, W = x2 - x1, H = bounds.Height }, maxW, maxH)
            });
        }

        return cols;
    }

    private static List<CellBox> BuildCells(List<TableRowBandInfo> rows, List<TableColumnBandInfo> cols, int maxW, int maxH)
    {
        var cells = new List<CellBox>(rows.Count * cols.Count);
        foreach (var row in rows)
        {
            foreach (var col in cols)
            {
                cells.Add(new CellBox
                {
                    RowIndex = row.RowIndex,
                    ColIndex = col.ColIndex,
                    Bbox = Clip(Intersect(row.Bbox, col.Bbox), maxW, maxH)
                });
            }
        }

        return cells;
    }

    private static List<TokenInfo> AssignTokensToCell(List<TokenInfo> tokens, BboxInfo cell)
    {
        var assigned = new List<TokenInfo>();
        foreach (var token in tokens)
        {
            var overlap = IntersectionArea(token.Bbox, cell);
            var tokenArea = Math.Max(1, token.Bbox.W * token.Bbox.H);
            var centerX = token.Bbox.X + token.Bbox.W / 2;
            var centerY = token.Bbox.Y + token.Bbox.H / 2;
            var centerInside = centerX >= cell.X && centerX <= cell.X + cell.W && centerY >= cell.Y && centerY <= cell.Y + cell.H;
            if (centerInside || overlap >= tokenArea * 0.35)
            {
                assigned.Add(token);
            }
        }

        return assigned
            .OrderBy(t => t.Bbox.Y)
            .ThenBy(t => t.Bbox.X)
            .ToList();
    }

    private static string ComposeCellText(List<TokenInfo> tokens)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var avgHeight = Math.Max(1, tokens.Average(t => t.Bbox.H));
        var lines = new List<List<TokenInfo>>();

        foreach (var token in tokens)
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

        var sb = new StringBuilder();
        foreach (var line in lines.OrderBy(l => l.Average(x => x.Bbox.Y)))
        {
            if (sb.Length > 0)
            {
                sb.Append(Environment.NewLine);
            }

            sb.Append(ComposeTokenSequence(line.OrderBy(t => t.Bbox.X).ToList()));
        }

        return sb.ToString().Trim();
    }

    private static string ComposeTokenSequence(IReadOnlyList<TokenInfo> tokens)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var text = tokens[0].Text?.Trim() ?? string.Empty;
        for (var index = 1; index < tokens.Count; index++)
        {
            var previousText = tokens[index - 1].Text?.Trim() ?? string.Empty;
            var currentText = tokens[index].Text?.Trim() ?? string.Empty;
            if (currentText.Length == 0)
            {
                continue;
            }

            if (text.Length > 0 && ShouldInsertSpace(tokens[index - 1], previousText, tokens[index], currentText))
            {
                text += " ";
            }

            text += currentText;
        }

        return text.Trim();
    }

    private static bool ShouldInsertSpace(TokenInfo previous, string previousText, TokenInfo current, string currentText)
    {
        if (previousText.Length == 0 || currentText.Length == 0)
        {
            return false;
        }

        var firstCurrent = currentText[0];
        var lastPrevious = previousText[^1];
        if (ClosingPunctuation.Contains(firstCurrent))
        {
            return false;
        }

        if (OpeningPunctuation.Contains(lastPrevious))
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
        return gap >= dynamicThreshold || gap >= relaxedThreshold;
    }

    private static List<string> BuildColumnKeys(IReadOnlyList<string> columnNames)
    {
        var keys = new List<string>(columnNames.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var col = 0; col < columnNames.Count; col++)
        {
            var text = columnNames[col];
            var baseKey = ToSnakeCase(text);
            if (string.IsNullOrWhiteSpace(baseKey) || string.Equals(baseKey, "column", StringComparison.OrdinalIgnoreCase))
            {
                baseKey = $"col_{col + 1}";
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

    private static TableCellNormalizedInfo NormalizeValue(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new TableCellNormalizedInfo { Type = "string", Value = null };
        }

        if (CurrencyRegex.IsMatch(trimmed))
        {
            var numeric = trimmed.TrimStart('$', '€', '£').Replace(",", string.Empty);
            _ = decimal.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out var value);
            return new TableCellNormalizedInfo { Type = "currency", Value = value, Currency = trimmed[0].ToString() };
        }

        if (DateLikeRegex.IsMatch(trimmed))
        {
            return new TableCellNormalizedInfo { Type = "date", Value = trimmed };
        }

        if (decimal.TryParse(trimmed.Replace(",", string.Empty), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return new TableCellNormalizedInfo { Type = "number", Value = number };
        }

        return new TableCellNormalizedInfo { Type = "string", Value = trimmed };
    }

    private static double AlphaRatio(List<string> texts)
    {
        if (texts.Count == 0)
        {
            return 0;
        }

        var alphaRich = texts.Count(t => t.Any(char.IsLetter) && t.Count(char.IsDigit) <= Math.Max(1, t.Length / 4));
        return (double)alphaRich / texts.Count;
    }

    private static double Regularity(List<int> values)
    {
        if (values.Count <= 1)
        {
            return 1;
        }

        var mean = values.Average();
        if (mean <= 0)
        {
            return 0;
        }

        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        var std = Math.Sqrt(variance);
        var cv = std / mean;
        return Math.Clamp(1.0 - cv, 0, 1);
    }

    private static int IntersectionArea(BboxInfo a, BboxInfo b)
    {
        var left = Math.Max(a.X, b.X);
        var right = Math.Min(a.X + a.W, b.X + b.W);
        var top = Math.Max(a.Y, b.Y);
        var bottom = Math.Min(a.Y + a.H, b.Y + b.H);

        if (right <= left || bottom <= top)
        {
            return 0;
        }

        return (right - left) * (bottom - top);
    }

    private static bool Overlaps(BboxInfo a, BboxInfo b) => IntersectionArea(a, b) > 0;

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

    private static BboxInfo Intersect(BboxInfo a, BboxInfo b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);

        if (x2 <= x1 || y2 <= y1)
        {
            return new BboxInfo { X = a.X, Y = a.Y, W = a.W, H = a.H };
        }

        return new BboxInfo
        {
            X = x1,
            Y = y1,
            W = x2 - x1,
            H = y2 - y1
        };
    }

    private static BboxInfo Clip(BboxInfo box, int maxW, int maxH)
    {
        var x = Math.Clamp(box.X, 0, Math.Max(0, maxW - 1));
        var y = Math.Clamp(box.Y, 0, Math.Max(0, maxH - 1));
        var right = Math.Clamp(box.X + box.W, x, maxW);
        var bottom = Math.Clamp(box.Y + box.H, y, maxH);

        return new BboxInfo
        {
            X = x,
            Y = y,
            W = Math.Max(0, right - x),
            H = Math.Max(0, bottom - y)
        };
    }

    private static List<TableInfo> RemoveNestedDuplicates(List<TableInfo> tables)
    {
        var ordered = tables
            .OrderByDescending(t => t.Bbox.W * t.Bbox.H)
            .ThenByDescending(t => t.Confidence)
            .ToList();

        var kept = new List<TableInfo>();
        foreach (var table in ordered)
        {
            if (kept.Any(existing => OverlapOverSmaller(existing.Bbox, table.Bbox) >= 0.8))
            {
                continue;
            }

            kept.Add(table);
        }

        return kept
            .OrderBy(t => t.Bbox.Y)
            .ThenBy(t => t.Bbox.X)
            .ToList();
    }

    private static List<TableOverlayInfo> RemoveNestedDuplicateOverlays(List<TableInfo> originalTables, List<TableOverlayInfo> originalOverlays)
    {
        var kept = RemoveNestedDuplicates(originalTables);
        var overlays = new List<TableOverlayInfo>(kept.Count);
        foreach (var table in kept)
        {
            var matchIndex = originalTables.FindIndex(t => t.Bbox.X == table.Bbox.X && t.Bbox.Y == table.Bbox.Y && t.Bbox.W == table.Bbox.W && t.Bbox.H == table.Bbox.H);
            overlays.Add(matchIndex >= 0 && matchIndex < originalOverlays.Count
                ? originalOverlays[matchIndex]
                : new TableOverlayInfo
                {
                    Method = table.Detection.Method,
                    TableBbox = table.Bbox,
                    RowBands = table.Grid.RowBands.Select(r => r.Bbox).ToList(),
                    ColBands = table.Grid.ColBands.Select(c => c.Bbox).ToList(),
                    Cells = table.Cells.Select(c => c.Bbox).ToList()
                });
        }

        return overlays;
    }

    private static double OverlapOverSmaller(BboxInfo a, BboxInfo b)
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
        var minArea = Math.Min(Math.Max(1, a.W * a.H), Math.Max(1, b.W * b.H));
        return (double)intersection / minArea;
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

    private sealed class CellBox
    {
        public int RowIndex { get; init; }
        public int ColIndex { get; init; }
        public required BboxInfo Bbox { get; init; }
    }
}
