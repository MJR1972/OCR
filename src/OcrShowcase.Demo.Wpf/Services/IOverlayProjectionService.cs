using Ocr.Core.Contracts;
using OcrShowcase.Demo.Wpf.Models;

namespace OcrShowcase.Demo.Wpf.Services;

public interface IOverlayProjectionService
{
    PreviewProjectionResult BuildPreviewProjection(OcrContractRoot contract);
}
