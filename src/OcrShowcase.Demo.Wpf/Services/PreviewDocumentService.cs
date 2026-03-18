using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Windows.Media.Imaging;
using Ghostscript.NET.Rasterizer;
using OcrShowcase.Demo.Wpf.Models;

namespace OcrShowcase.Demo.Wpf.Services;

public sealed class PreviewDocumentService : IPreviewDocumentService
{
    public Task<IReadOnlyList<PreviewProjectionResult>> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => LoadInternal(filePath, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<PreviewProjectionResult> LoadInternal(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return [];
        }

        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)
            ? LoadPdfPreviewPages(filePath, cancellationToken)
            : LoadImagePreviewPage(filePath);
    }

    private static IReadOnlyList<PreviewProjectionResult> LoadImagePreviewPage(string filePath)
    {
        var dimensions = TryReadImageDimensions(filePath);
        return
        [
            new PreviewProjectionResult(
                1,
                filePath,
                dimensions.width,
                dimensions.height,
                [])
        ];
    }

    private static IReadOnlyList<PreviewProjectionResult> LoadPdfPreviewPages(string filePath, CancellationToken cancellationToken)
    {
        var pages = new List<PreviewProjectionResult>();
        var cacheFolder = GetCacheFolder(filePath);
        Directory.CreateDirectory(cacheFolder);

        using var rasterizer = new GhostscriptRasterizer();
        rasterizer.Open(filePath);

        for (var pageNumber = 1; pageNumber <= rasterizer.PageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cachePath = Path.Combine(cacheFolder, $"page_{pageNumber}.png");
            if (!File.Exists(cachePath))
            {
                using var rasterImage = rasterizer.GetPage(160, pageNumber);
                File.WriteAllBytes(cachePath, RasterImageToPngBytes(rasterImage));
            }

            var dimensions = TryReadImageDimensions(cachePath);
            pages.Add(new PreviewProjectionResult(
                pageNumber,
                cachePath,
                dimensions.width,
                dimensions.height,
                []));
        }

        return pages;
    }

    private static string GetCacheFolder(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var identity = $"{fileInfo.FullName}|{fileInfo.LastWriteTimeUtc.Ticks}|{fileInfo.Length}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var hash = Convert.ToHexString(hashBytes[..8]);
        var safeName = Path.GetFileNameWithoutExtension(filePath);
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidChar, '_');
        }

        return Path.Combine(Path.GetTempPath(), "OcrShowcase.Demo.Wpf", "preview-cache", $"{safeName}_{hash}");
    }

    private static (double width, double height) TryReadImageDimensions(string imagePath)
    {
        try
        {
            using var stream = File.OpenRead(imagePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames.FirstOrDefault();
            return frame is null ? (1200, 1600) : (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (1200, 1600);
        }
    }

    private static byte[] RasterImageToPngBytes(object rasterImage)
    {
        var bitmapType = rasterImage.GetType();
        var skiaAssembly = bitmapType.Assembly;
        var skImageType = skiaAssembly.GetType("SkiaSharp.SKImage")
            ?? throw new InvalidOperationException("SkiaSharp.SKImage type not found.");
        var skDataType = skiaAssembly.GetType("SkiaSharp.SKData")
            ?? throw new InvalidOperationException("SkiaSharp.SKData type not found.");
        var skEncodedImageFormatType = skiaAssembly.GetType("SkiaSharp.SKEncodedImageFormat")
            ?? throw new InvalidOperationException("SkiaSharp.SKEncodedImageFormat type not found.");

        var fromBitmap = skImageType.GetMethod("FromBitmap", BindingFlags.Public | BindingFlags.Static, null, [bitmapType], null)
            ?? throw new InvalidOperationException("SkiaSharp.SKImage.FromBitmap was not found.");
        var encode = skImageType.GetMethod("Encode", BindingFlags.Public | BindingFlags.Instance, null, [skEncodedImageFormatType, typeof(int)], null)
            ?? throw new InvalidOperationException("SkiaSharp.SKImage.Encode(SKEncodedImageFormat, int) was not found.");
        var toArray = skDataType.GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SkiaSharp.SKData.ToArray() was not found.");

        var pngFormat = Enum.Parse(skEncodedImageFormatType, "Png");

        using var skImage = (IDisposable?)fromBitmap.Invoke(null, [rasterImage])
            ?? throw new InvalidOperationException("Failed to create SKImage from rasterized PDF page.");
        using var skData = (IDisposable?)encode.Invoke(skImage, [pngFormat, 100])
            ?? throw new InvalidOperationException("Failed to encode rasterized PDF page as PNG.");

        return (byte[]?)toArray.Invoke(skData, null)
            ?? throw new InvalidOperationException("Failed to read PNG bytes from SKData.");
    }
}
