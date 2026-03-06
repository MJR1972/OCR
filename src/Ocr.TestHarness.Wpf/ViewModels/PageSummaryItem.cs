namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed class PageSummaryItem
{
    public int PageIndex { get; init; }
    public int TokenCount { get; init; }
    public double MeanConfidence { get; init; }
    public int RenderMs { get; init; }
    public int PreprocessMs { get; init; }
    public int OcrMs { get; init; }
    public int LayoutMs { get; init; }
    public int TableCount { get; init; }
    public double MeanTableConfidence { get; init; }
}
