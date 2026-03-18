namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed class FieldSummaryItem
{
    public string FieldId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public int PageIndex { get; init; }
    public string SourceMethod { get; init; } = string.Empty;
    public bool NeedsReview { get; init; }
    public int ValidationIssueCount { get; init; }
}
