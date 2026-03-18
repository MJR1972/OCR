using Ocr.Core.Contracts;

namespace OcrShowcase.Demo.Wpf.Services;

public interface IOcrDemoService
{
    Task<OcrDemoRunResult> RunAsync(string filePath, CancellationToken cancellationToken = default);
}

public sealed record OcrDemoRunResult(
    string Json,
    string? OutputJsonPath,
    OcrContractRoot Contract);
