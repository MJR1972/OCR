namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed record PreviewOverlayItem
{
    public required string Kind { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public string? Label { get; init; }

    public string? RecognizedText { get; init; }

    public string? ConfidenceText { get; init; }

    public string? PageText { get; init; }

    public bool SupportsTooltip { get; init; }

    public bool HasTooltip =>
        SupportsTooltip && (
            !string.IsNullOrWhiteSpace(RecognizedText) ||
            !string.IsNullOrWhiteSpace(ConfidenceText) ||
            !string.IsNullOrWhiteSpace(PageText));
}
