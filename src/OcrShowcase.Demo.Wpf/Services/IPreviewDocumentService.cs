using OcrShowcase.Demo.Wpf.Models;

namespace OcrShowcase.Demo.Wpf.Services;

public interface IPreviewDocumentService
{
    Task<IReadOnlyList<PreviewProjectionResult>> LoadAsync(string filePath, CancellationToken cancellationToken = default);
}
