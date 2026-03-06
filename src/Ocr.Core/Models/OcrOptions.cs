namespace Ocr.Core.Models;

public sealed record OcrOptions
{
    public int TargetDpi { get; init; } = 300;
    public string Language { get; init; } = "eng";
    public int? PageSegMode { get; init; }
    public int? EngineMode { get; init; }
    public bool PreserveInterwordSpaces { get; init; } = true;
    public bool SaveTokenOverlay { get; init; } = false;
    public bool EnableNoiseFiltering { get; init; } = false;

    // Deskew enabled by default (Phase 2+). In Phase 1 we only record options in JSON.
    public bool EnableDeskew { get; init; } = true;
    public double MaxDeskewDegrees { get; init; } = 40.0;
    public double DeskewAngleStep { get; init; } = 0.5;
    public double MinDeskewConfidence { get; init; } = 0.15;

    // Optional preprocessing (OFF by default)
    public bool EnableDenoise { get; init; } = false;
    public string DenoiseMethod { get; init; } = "median";
    public int DenoiseKernel { get; init; } = 3;

    public bool EnableBinarization { get; init; } = false;
    public string BinarizationMethod { get; init; } = "otsu";

    public bool EnableContrastEnhancement { get; init; } = false;
    public string ContrastMethod { get; init; } = "clahe";

    // Output behavior
    public bool SaveJsonToDisk { get; init; } = true;
    public string? OutputFolder { get; init; }

    // Debug artifacts (Phase 3+)
    public bool SaveDebugArtifacts { get; init; } = false;

    // A/B testing label
    public string ProfileName { get; init; } = "default";
}
