using Newtonsoft.Json;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;
using Ocr.Core.Models;

namespace OcrShowcase.Demo.Wpf.Services;

public sealed class OcrDemoService : IOcrDemoService
{
    private readonly IOcrProcessor _ocrProcessor;

    public OcrDemoService(IOcrProcessor ocrProcessor)
    {
        _ocrProcessor = ocrProcessor;
    }

    public async Task<OcrDemoRunResult> RunAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A source file is required.", nameof(filePath));
        }

        var options = new OcrOptions
        {
            SaveJsonToDisk = true,
            SaveDebugArtifacts = true,
            SaveTokenOverlay = false,
            ProfileName = "showcase-demo"
        };

        var result = await Task.Run(() => _ocrProcessor.ProcessFile(filePath, options, cancellationToken), cancellationToken);
        var contract = JsonConvert.DeserializeObject<OcrContractRoot>(result.Json);

        if (contract is null)
        {
            throw new InvalidOperationException("The OCR engine returned an empty or unreadable contract.");
        }

        return new OcrDemoRunResult(result.Json, result.OutputJsonPath, contract);
    }
}
