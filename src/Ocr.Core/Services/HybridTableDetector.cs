using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;
using OpenCvSharp;

namespace Ocr.Core.Services;

public sealed class HybridTableDetector : ITableDetector
{
    private readonly ITableDetector _layoutDetector;
    private readonly ITableDetector _gridlineDetector;

    public HybridTableDetector()
        : this(new LayoutTableDetector(), new GridlineTableDetector())
    {
    }

    public HybridTableDetector(ITableDetector layoutDetector, ITableDetector gridlineDetector)
    {
        _layoutDetector = layoutDetector;
        _gridlineDetector = gridlineDetector;
    }

    public TableDetectionResult Detect(PageInfo page, Mat pageImage)
    {
        var gridlineResult = _gridlineDetector.Detect(page, pageImage);
        if (gridlineResult.Tables.Count > 0)
        {
            return Normalize(gridlineResult, "lines");
        }

        var layoutResult = _layoutDetector.Detect(page, pageImage);
        return Normalize(layoutResult, "layout");
    }

    private static TableDetectionResult Normalize(TableDetectionResult result, string fallbackMethod)
    {
        var ordered = result.Tables
            .Select((table, index) => new { Table = table, Index = index })
            .OrderBy(x => x.Table.Bbox.Y)
            .ThenBy(x => x.Table.Bbox.X)
            .ToList();

        var tables = new List<TableInfo>(ordered.Count);
        var overlays = new List<TableOverlayInfo>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var table = ordered[i].Table;
            table.TableId = $"tbl-{i + 1:0000}";
            if (string.IsNullOrWhiteSpace(table.Detection.Method))
            {
                table.Detection.Method = fallbackMethod;
            }

            tables.Add(table);

            if (ordered[i].Index < result.Overlays.Count)
            {
                overlays.Add(result.Overlays[ordered[i].Index]);
            }
            else
            {
                overlays.Add(new TableOverlayInfo
                {
                    Method = table.Detection.Method,
                    PageIndex = 0,
                    TableBbox = table.Bbox,
                    RowBands = table.Grid.RowBands.Select(r => r.Bbox).ToList(),
                    ColBands = table.Grid.ColBands.Select(c => c.Bbox).ToList(),
                    Cells = table.Cells.Select(c => c.Bbox).ToList()
                });
            }
        }

        return new TableDetectionResult
        {
            Tables = tables,
            Overlays = overlays
        };
    }
}
