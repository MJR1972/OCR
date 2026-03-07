using Ocr.Core.Contracts;
using OpenCvSharp;

namespace Ocr.Core.Abstractions;

public interface IRegionDetector
{
    RegionDetectionResult Detect(PageInfo page, Mat pageImage);
}

public sealed class RegionDetectionResult
{
    public List<RegionInfo> Regions { get; init; } = [];
    public List<RegionOverlayInfo> Overlays { get; init; } = [];
    public RegionDetectionDiagnostics Diagnostics { get; init; } = new();
}

public sealed class RegionOverlayInfo
{
    public string Type { get; init; } = "checkbox";
    public bool? Value { get; init; }
    public BboxInfo Bbox { get; init; } = new();
}

public sealed class RegionDetectionDiagnostics
{
    public int RawCandidateCount { get; init; }
    public int GeometryFilteredCount { get; init; }
    public int LabelFilteredCount { get; init; }
    public int FinalCheckboxCount { get; init; }
    public int FinalRadioCount { get; init; }
}
