namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed class TableCellDisplayItem
{
    public int RowIndex { get; init; }
    public int ColIndex { get; init; }
    public string Text { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public int TokenCount { get; init; }
}
