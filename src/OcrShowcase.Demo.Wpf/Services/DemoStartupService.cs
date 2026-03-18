using System.IO;
using Newtonsoft.Json;
using Ocr.Core.Contracts;

namespace OcrShowcase.Demo.Wpf.Services;

public sealed class DemoStartupService : IDemoStartupService
{
    public DemoStartupPayload? TryLoadDemoStartup()
    {
        var sampleFolder = FindBestSampleFolder();
        if (sampleFolder is null)
        {
            return null;
        }

        var resultJsonPath = Path.Combine(sampleFolder, "result.json");
        if (!File.Exists(resultJsonPath))
        {
            return null;
        }

        var json = File.ReadAllText(resultJsonPath);
        var contract = JsonConvert.DeserializeObject<OcrContractRoot>(json);
        if (contract is null)
        {
            return null;
        }

        return new DemoStartupPayload(
            sampleFolder,
            new OcrDemoRunResult(json, resultJsonPath, contract));
    }

    private static string? FindBestSampleFolder()
    {
        foreach (var root in EnumerateCandidateRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var preferred = Directory.GetDirectories(root, "OCR Example Doc_*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .OrderByDescending(dir => HasPreviewArtifacts(dir.FullName))
                .ThenByDescending(dir => dir.LastWriteTimeUtc)
                .FirstOrDefault();

            if (preferred is not null && File.Exists(Path.Combine(preferred.FullName, "result.json")))
            {
                return preferred.FullName;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateRoots()
    {
        var baseDir = AppContext.BaseDirectory;

        yield return Path.GetFullPath(Path.Combine(baseDir, "output"));
        yield return Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Ocr.TestHarness.Wpf\bin\Debug\net10.0-windows\output"));
        yield return Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\OcrShowcase.Demo.Wpf\bin\Debug\net10.0-windows\output"));
    }

    private static bool HasPreviewArtifacts(string folder)
    {
        return File.Exists(Path.Combine(folder, "page_1_original_or_render.png")) ||
               File.Exists(Path.Combine(folder, "page_1_preprocessed.png"));
    }
}
