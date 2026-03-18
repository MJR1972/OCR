namespace Ocr.Core.Models;

public sealed class OcrResult
{
    public string Json { get; init; } = string.Empty;
    public string? OutputJsonPath { get; init; }
}