namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed class TableHeaderColumnItem
{
    public int ColIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public double Confidence { get; init; }
}
