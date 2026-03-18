using System.IO;
using Ocr.Core.Contracts;
using OcrShowcase.Demo.Wpf.Models;

namespace OcrShowcase.Demo.Wpf.Services;

public sealed class OverlayProjectionService : IOverlayProjectionService
{
    public IReadOnlyList<PreviewProjectionResult> BuildPreviewProjections(OcrContractRoot contract)
    {
        if (contract.Pages.Count == 0)
        {
            return [new PreviewProjectionResult(1, null, 1200, 1600, [])];
        }

        return contract.Pages
            .OrderBy(p => p.PageIndex)
            .Select(page =>
            {
                var width = page.Size.WidthPx > 0 ? page.Size.WidthPx : 1200;
                var height = page.Size.HeightPx > 0 ? page.Size.HeightPx : 1600;
                var imagePath = ResolveImagePath(contract, page);
                var overlayItems = new List<PreviewOverlayItem>();

                overlayItems.AddRange(page.Tokens
                    .Where(token => IsValid(token.Bbox))
                    .Select(token => new PreviewOverlayItem
                    {
                        Kind = "Word",
                        X = token.Bbox.X,
                        Y = token.Bbox.Y,
                        Width = token.Bbox.W,
                        Height = token.Bbox.H,
                        RecognizedText = token.Text,
                        ConfidenceText = token.Confidence > 0 ? $"{token.Confidence * 100:F1}%" : null,
                        PageText = page.PageIndex > 0 ? $"Page {page.PageIndex}" : null,
                        SupportsTooltip = true
                    }));

                // "Show Fields" intentionally represents promoted structured field regions only.
                // Documents with zero promoted fields will therefore show no field overlays even if
                // token boxes and table regions are available.
                overlayItems.AddRange(contract.Recognition.Fields
                    .Where(field => field.Source.PageIndex == page.PageIndex && IsValid(field.Source.Bbox))
                    .Select(field => new PreviewOverlayItem
                    {
                        Kind = "Field",
                        X = field.Source.Bbox.X,
                        Y = field.Source.Bbox.Y,
                        Width = field.Source.Bbox.W,
                        Height = field.Source.Bbox.H,
                        Label = string.IsNullOrWhiteSpace(field.Label) ? field.FieldId : field.Label,
                        RecognizedText = field.Value?.ToString() ?? field.Normalized.Value?.ToString(),
                        ConfidenceText = field.Confidence > 0 ? $"{field.Confidence * 100:F1}%" : null,
                        PageText = field.Source.PageIndex > 0 ? $"Page {field.Source.PageIndex}" : null
                    }));

                overlayItems.AddRange(page.Tables
                    .Where(table => IsValid(table.Bbox))
                    .Select(table => new PreviewOverlayItem
                    {
                        Kind = "Table",
                        X = table.Bbox.X,
                        Y = table.Bbox.Y,
                        Width = table.Bbox.W,
                        Height = table.Bbox.H,
                        Label = string.IsNullOrWhiteSpace(table.TableId) ? table.Detection.Method : table.TableId,
                        ConfidenceText = table.Confidence > 0 ? $"{table.Confidence * 100:F1}%" : null,
                        PageText = page.PageIndex > 0 ? $"Page {page.PageIndex}" : null
                    }));

                return new PreviewProjectionResult(page.PageIndex, imagePath, width, height, overlayItems);
            })
            .ToList();
    }

    private static string? ResolveImagePath(OcrContractRoot contract, PageInfo page)
    {
        if (!string.IsNullOrWhiteSpace(page.Artifacts.PageImageRef) && File.Exists(page.Artifacts.PageImageRef))
        {
            return page.Artifacts.PageImageRef;
        }

        var debugArtifact = contract.Extensions.DebugArtifactPaths.FirstOrDefault(path => path.PageIndex == page.PageIndex);
        var candidates = new[]
        {
            debugArtifact?.PreprocessedPath,
            debugArtifact?.OriginalOrRenderPath,
            debugArtifact?.GrayPath
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static bool IsValid(BboxInfo bbox)
    {
        return bbox.W > 0 && bbox.H > 0;
    }
}
