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
}

public sealed class RegionOverlayInfo
{
    public string Type { get; init; } = "checkbox";
    public bool? Value { get; init; }
    public BboxInfo Bbox { get; init; } = new();
}
