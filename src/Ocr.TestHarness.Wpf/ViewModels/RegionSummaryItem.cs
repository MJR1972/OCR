namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed class RegionSummaryItem
{
    public int PageIndex { get; init; }
    public string RegionId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public int LabelTokenCount { get; init; }
    public string Bbox { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}
