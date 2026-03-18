namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed class TableSummaryItem
{
    public int PageIndex { get; init; }
    public string TableId { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public int RowCount { get; init; }
    public int ColumnCount { get; init; }
}
