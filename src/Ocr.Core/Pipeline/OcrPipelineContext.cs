using Ocr.Core.Contracts;
using Ocr.Core.Models;

namespace Ocr.Core.Pipeline;

internal sealed class OcrPipelineContext
{
    public OcrPipelineContext(string filePath, OcrOptions options, string fileType, string mimeType, OcrContractRoot root)
    {
        FilePath = filePath;
        Options = options;
        FileType = fileType;
        MimeType = mimeType;
        Root = root;
    }

    public string FilePath { get; }
    public OcrOptions Options { get; }
    public string FileType { get; }
    public string MimeType { get; }
    public OcrContractRoot Root { get; }

    public string TessdataPath { get; set; } = string.Empty;
    public string? RunOutputFolder { get; set; }
    public int LoadedPageCount { get; set; }

    // Internal stage handoff data for expensive objects/results.
    public Dictionary<string, object?> Items { get; } = new(StringComparer.Ordinal);
    public List<PipelineStageTiming> StageTimings { get; } = [];
}

internal sealed class PipelineStageTiming
{
    public string StageName { get; init; } = string.Empty;
    public int DurationMs { get; init; }
    public string Status { get; init; } = "completed";
    public string? Note { get; init; }
}

internal static class OcrPipelineStageNames
{
    public const string InputLoad = "Input/Load";
    public const string Render = "Render";
    public const string Preprocess = "Preprocess";
    public const string OcrExtraction = "OCR Extraction";
    public const string TokenCleanup = "Token Cleanup";
    public const string LineReconstruction = "Line Reconstruction";
    public const string LayoutAnalysis = "Layout Analysis";
    public const string TableDetection = "Table Detection";
    public const string RegionDetection = "Region Detection";
    public const string StructuredFieldExtraction = "Structured Field Extraction";
    public const string FinalAssembly = "Final Result Assembly";
}
