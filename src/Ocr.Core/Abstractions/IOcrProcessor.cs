using Ocr.Core.Models;

namespace Ocr.Core.Abstractions;

public interface IOcrProcessor
{
    OcrResult ProcessFile(string filePath, OcrOptions? options = null, CancellationToken ct = default);
}