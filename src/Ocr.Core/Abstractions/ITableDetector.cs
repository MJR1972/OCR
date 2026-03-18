using Ocr.Core.Contracts;
using OpenCvSharp;

namespace Ocr.Core.Abstractions;

public interface ITableDetector
{
    TableDetectionResult Detect(PageInfo page, Mat pageImage);
}

public sealed class TableDetectionResult
{
    public List<TableInfo> Tables { get; init; } = [];
    public List<TableOverlayInfo> Overlays { get; init; } = [];
}

public sealed class TableOverlayInfo
{
    public int PageIndex { get; init; }
    public string Method { get; init; } = "layout";
    public BboxInfo TableBbox { get; init; } = new();
    public List<BboxInfo> RowBands { get; init; } = [];
    public List<BboxInfo> ColBands { get; init; } = [];
    public List<BboxInfo> Cells { get; init; } = [];
    public List<int> HorizontalLinesY { get; init; } = [];
    public List<int> VerticalLinesX { get; init; } = [];
}
