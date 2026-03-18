namespace Ocr.Core.Contracts;

public sealed class OcrContractRoot
{
    public SchemaInfo Schema { get; set; } = new();
    public DefinitionsInfo Definitions { get; set; } = new();
    public DocumentInfo Document { get; set; } = new();
    public MetricsInfo Metrics { get; set; } = new();
    public List<PageInfo> Pages { get; set; } = [];
    public RecognitionInfo Recognition { get; set; } = new();
    public ReviewInfo Review { get; set; } = new();
    public List<IssueInfo> Warnings { get; set; } = [];
    public List<IssueInfo> Errors { get; set; } = [];
    public List<string> DocumentWords { get; set; } = [];
    public ExtensionsInfo Extensions { get; set; } = new();
}

public sealed class SchemaInfo
{
    public string Name { get; set; } = "com.yourcompany.ocr.document";
    public string Version { get; set; } = "1.1.0";
    public string Status { get; set; } = "draft";
}

public sealed class DefinitionsInfo
{
    public CoordinateSystemInfo CoordinateSystem { get; set; } = new();
    public ConfidenceDefinitionInfo Confidence { get; set; } = new();
    public ReadingOrderDefinitionInfo ReadingOrder { get; set; } = new();
    public EnumDefinitionsInfo Enums { get; set; } = new();
}

public sealed class CoordinateSystemInfo
{
    public string Unit { get; set; } = "px";
    public string Origin { get; set; } = "top-left";
    public string XDirection { get; set; } = "right";
    public string YDirection { get; set; } = "down";
    public string BboxFormat { get; set; } = "x,y,w,h";
    public bool PageRotationAppliedToCoords { get; set; } = true;
    public string Notes { get; set; } = "All bbox coordinates are relative to the final, processed page image (after render + rotation + deskew).";
}

public sealed class ConfidenceDefinitionInfo
{
    public string Scale { get; set; } = "0to1";
    public double LowTokenThreshold { get; set; } = 0.7;
    public double LowLineThreshold { get; set; } = 0.75;
    public double LowBlockThreshold { get; set; } = 0.8;
    public double LowCellThreshold { get; set; } = 0.75;
    public double LowTableThreshold { get; set; } = 0.8;
    public double LowFieldThreshold { get; set; } = 0.8;
}

public sealed class ReadingOrderDefinitionInfo
{
    public string Default { get; set; } = "top-to-bottom-left-to-right";
    public List<string> Supported { get; set; } = ["top-to-bottom-left-to-right", "top-to-bottom-right-to-left", "custom"];
}

public sealed class EnumDefinitionsInfo
{
    public List<string> Severity { get; set; } = ["info", "warning", "error", "fatal"];
    public List<string> TokenType { get; set; } = ["word", "punct", "number", "symbol", "unknown"];
    public List<string> BlockType { get; set; } = ["text", "table", "image", "barcode", "signature", "stamp", "unknown"];
    public List<string> RegionType { get; set; } = ["checkbox", "radio", "signature", "barcode", "qr", "image", "stamp", "unknown"];
    public List<string> NormalizedType { get; set; } = ["string", "number", "date", "currency", "percent", "boolean", "unknown"];
    public List<string> TableDetectionMethod { get; set; } = ["lines", "layout", "lines+layout", "model", "unknown"];
    public List<string> FieldSourceMethod { get; set; } = ["ocr", "ocr+layout", "ocr+regex", "table", "model", "human"];
}

public sealed class DocumentInfo
{
    public string DocumentId { get; set; } = "00000000-0000-0000-0000-000000000000";
    public string CorrelationId { get; set; } = "optional-run-id-for-tracing";
    public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    public SourceInfo Source { get; set; } = new();
    public ProcessingInfo Processing { get; set; } = new();
}

public sealed class SourceInfo
{
    public string OriginalFileName { get; set; } = "input.pdf";
    public string FileType { get; set; } = "pdf";
    public string? FileHashSha256 { get; set; }
    public int PageCount { get; set; } = 1;
    public string MimeType { get; set; } = "application/pdf";
}

public sealed class ProcessingInfo
{
    public string PipelineVersion { get; set; } = "ocr-pipeline-1.1.0";
    public EngineInfo Engine { get; set; } = new();
    public RenderInfo Render { get; set; } = new();
    public PreprocessingInfo Preprocessing { get; set; } = new();
    public PostprocessingInfo Postprocessing { get; set; } = new();
    public ConfidencePolicyInfo ConfidencePolicy { get; set; } = new();
}

public sealed class EngineInfo
{
    public string Name { get; set; } = "tesseract";
    public string Version { get; set; } = "5.x";
    public List<string> Language { get; set; } = ["eng"];
    public EngineParamsInfo Params { get; set; } = new();
}

public sealed class EngineParamsInfo
{
    public string Psm { get; set; } = "optional";
    public string Oem { get; set; } = "optional";
}

public sealed class RenderInfo
{
    public int DpiOriginal { get; set; } = 300;
    public int DpiNormalizedTo { get; set; } = 300;
    public string ColorMode { get; set; } = "grayscale";
    public string PageImageFormat { get; set; } = "png";
}

public sealed class PreprocessingInfo
{
    public RotationInfo Rotation { get; set; } = new();
    public DeskewInfo Deskew { get; set; } = new();
    public MethodToggleInfo Denoise { get; set; } = new();
    public MethodToggleInfo Binarization { get; set; } = new();
    public MethodToggleInfo ContrastEnhancement { get; set; } = new();
}

public sealed class RotationInfo
{
    public bool Attempted { get; set; }
    public bool Applied { get; set; }
    public double Degrees { get; set; }
    public double Confidence { get; set; }
}

public sealed class DeskewInfo
{
    public bool Attempted { get; set; } = true;
    public bool Applied { get; set; }
    public double Degrees { get; set; }
    public double Confidence { get; set; }
}

public sealed class MethodToggleInfo
{
    public bool Enabled { get; set; }
    public string? Method { get; set; }
}

public sealed class PostprocessingInfo
{
    public bool FixCommonOcrErrors { get; set; }
    public bool NormalizeWhitespace { get; set; } = true;
}

public sealed class ConfidencePolicyInfo
{
    public string Scale { get; set; } = "0to1";
    public double LowTokenThreshold { get; set; } = 0.7;
    public AggregationInfo Aggregation { get; set; } = new();
}

public sealed class AggregationInfo
{
    public string LineConfidence { get; set; } = "mean(tokens.confidence)";
    public string BlockConfidence { get; set; } = "mean(lines.confidence)";
    public string CellConfidence { get; set; } = "mean(tokenIds.confidence)";
    public string RowConfidence { get; set; } = "mean(cells.confidence)";
    public string TableConfidence { get; set; } = "mean(rows.confidence)";
    public string FieldConfidence { get; set; } = "mean(tokenIds.confidence)";
}

public sealed class MetricsInfo
{
    public int TotalMs { get; set; }
    public int DocumentOcrMs { get; set; }
    public List<int> PagesMs { get; set; } = [];
    public BreakdownInfo BreakdownMs { get; set; } = new();
}

public sealed class BreakdownInfo
{
    public int RenderMs { get; set; }
    public int PreprocessMs { get; set; }
    public int OcrMs { get; set; }
    public int LayoutMs { get; set; }
    public int TableDetectMs { get; set; }
    public int RecognitionMs { get; set; }
    public int PostprocessMs { get; set; }
}

public sealed class PageInfo
{
    public int PageIndex { get; set; } = 1;
    public string PageId { get; set; } = "page-1";
    public PageSizeInfo Size { get; set; } = new();
    public PageTimingInfo Timing { get; set; } = new();
    public PageQualityInfo Quality { get; set; } = new();
    public PageTextInfo Text { get; set; } = new();
    public List<TokenInfo> Tokens { get; set; } = [];
    public List<LineInfo> Lines { get; set; } = [];
    public List<BlockInfo> Blocks { get; set; } = [];
    public List<TableInfo> Tables { get; set; } = [];
    public List<KeyValueCandidateInfo> KeyValueCandidates { get; set; } = [];
    public List<RegionInfo> Regions { get; set; } = [];
    public List<string> PageWords { get; set; } = [];
    public List<string> UnassignedTokenIds { get; set; } = [];
    public ArtifactsInfo Artifacts { get; set; } = new();
}

public sealed class TokenInfo
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "word";
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public double? ConfidenceRaw { get; set; }
    public bool IsLowConfidence { get; set; }
    public BboxInfo Bbox { get; set; } = new();
    public string BlockId { get; set; } = string.Empty;
    public string LineId { get; set; } = string.Empty;
    public List<TokenAlternateInfo> Alternates { get; set; } = [];
    public TokenSourceInfo Source { get; set; } = new();
}

public sealed class TokenAlternateInfo
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
}

public sealed class TokenSourceInfo
{
    public string Engine { get; set; } = "tesseract";
    public string Level { get; set; } = "word";
}

public sealed class LineInfo
{
    public string LineId { get; set; } = string.Empty;
    public BboxInfo Bbox { get; set; } = new();
    public List<string> TokenIds { get; set; } = [];
    public double Confidence { get; set; }
    public bool IsLowConfidence { get; set; }
}

public sealed class BlockInfo
{
    public string BlockId { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public BboxInfo Bbox { get; set; } = new();
    public List<string> LineIds { get; set; } = [];
    public List<string> TokenIds { get; set; } = [];
    public double Confidence { get; set; }
    public bool IsLowConfidence { get; set; }
}

public sealed class BboxInfo
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public sealed class PageSizeInfo
{
    public int WidthPx { get; set; } = 2550;
    public int HeightPx { get; set; } = 3300;
    public int Dpi { get; set; } = 300;
    public double RotationDegrees { get; set; }
}

public sealed class PageTimingInfo
{
    public int RenderMs { get; set; }
    public int PreprocessMs { get; set; }
    public int OcrMs { get; set; }
    public int LayoutMs { get; set; }
    public int TableDetectMs { get; set; }
    public int PostprocessMs { get; set; }
}

public sealed class PageQualityInfo
{
    public double MeanTokenConfidence { get; set; }
    public int LowConfidenceTokenCount { get; set; }
    public bool BlankPage { get; set; }
}

public sealed class PageTextInfo
{
    public string FullText { get; set; } = string.Empty;
    public string ReadingOrder { get; set; } = "top-to-bottom-left-to-right";
}

public sealed class ArtifactsInfo
{
    public string? PageImageRef { get; set; }
    public string? DebugOverlayRef { get; set; }
}

public sealed class RegionInfo
{
    public string RegionId { get; set; } = string.Empty;
    public string Type { get; set; } = "checkbox";
    public BboxInfo Bbox { get; set; } = new();
    public double Confidence { get; set; }
    public bool? Value { get; set; }
    public List<string> LabelTokenIds { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public sealed class RecognitionInfo
{
    public DocumentTypeInfo DocumentType { get; set; } = new();
    public AnchorsInfo Anchors { get; set; } = new();
    public List<RecognitionFieldInfo> Fields { get; set; } = [];
    public List<object> TableMappings { get; set; } = [];
}

public sealed class DocumentTypeInfo
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public double Confidence { get; set; }
}

public sealed class AnchorsInfo
{
    public List<object> MatchedAnchors { get; set; } = [];
    public List<object> UnmatchedAnchors { get; set; } = [];
}

public sealed class ReviewInfo
{
    public bool Required { get; set; }
    public string? ReviewedBy { get; set; }
    public string? ReviewedUtc { get; set; }
    public List<object> Changes { get; set; } = [];
}

public sealed class IssueInfo
{
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
    public int? PageIndex { get; set; }
    public Dictionary<string, object?> Details { get; set; } = [];
}

public sealed class ExtensionsInfo
{
    public string TessdataPath { get; set; } = string.Empty;
    public OptionSnapshotInfo OptionSnapshot { get; set; } = new();
    public List<PagePreprocessingInfo> PagePreprocessing { get; set; } = [];
    public List<PageDebugArtifactInfo> DebugArtifactPaths { get; set; } = [];
    public List<PageNoiseDiagnosticsInfo> PageNoiseDiagnostics { get; set; } = [];
    public List<LineReconstructionDiagnosticsInfo> LineReconstructionDiagnostics { get; set; } = [];
    public List<TokenCleanupStatsInfo> TokenCleanupStats { get; set; } = [];
    public List<string> FilteredTokenIds { get; set; } = [];
    public List<FieldExtractionDiagnosticsInfo> FieldExtractionDiagnostics { get; set; } = [];
    public StructuredFieldExtractionStatsInfo StructuredFieldExtractionStats { get; set; } = new();
    public List<PipelineStageTimingInfo> PipelineStageTimings { get; set; } = [];
}

public sealed class KeyValueCandidateInfo
{
    public string PairId { get; set; } = string.Empty;
    public KeyValuePartInfo Label { get; set; } = new();
    public KeyValuePartInfo Value { get; set; } = new();
    public double Confidence { get; set; }
    public string Method { get; set; } = "ocr+layout";
}

public sealed class KeyValuePartInfo
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BboxInfo Bbox { get; set; } = new();
    public List<string> TokenIds { get; set; } = [];
}

public sealed class RecognitionFieldInfo
{
    public string FieldId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public object? Value { get; set; }
    public TableCellNormalizedInfo Normalized { get; set; } = new();
    public double Confidence { get; set; }
    public bool IsLowConfidence { get; set; }
    public FieldSourceInfo Source { get; set; } = new();
    public FieldValidationInfo Validation { get; set; } = new();
    public FieldReviewInfo Review { get; set; } = new();
}

public sealed class FieldSourceInfo
{
    public int PageIndex { get; set; }
    public BboxInfo Bbox { get; set; } = new();
    public List<string> TokenIds { get; set; } = [];
    public string Method { get; set; } = "ocr+layout";
}

public sealed class FieldValidationInfo
{
    public List<string> RulesApplied { get; set; } = [];
    public bool Validated { get; set; }
    public List<IssueInfo> Issues { get; set; } = [];
}

public sealed class FieldReviewInfo
{
    public bool NeedsReview { get; set; }
    public string? Reason { get; set; }
}

public sealed class TableInfo
{
    public string TableId { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BboxInfo Bbox { get; set; } = new();
    public TableDetectionInfo Detection { get; set; } = new();
    public TableGridInfo Grid { get; set; } = new();
    public TableHeaderInfo Header { get; set; } = new();
    public List<TableCellInfo> Cells { get; set; } = [];
    public List<TableRowInfo> Rows { get; set; } = [];
    public TableTokenCoverageInfo TokenCoverage { get; set; } = new();
    public List<IssueInfo> Issues { get; set; } = [];
}

public sealed class TableDetectionInfo
{
    public string Method { get; set; } = "layout";
    public bool HasExplicitGridLines { get; set; }
    public string? ModelName { get; set; }
    public string? ModelVersion { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed class TableGridInfo
{
    public int Rows { get; set; }
    public int Cols { get; set; }
    public List<TableRowBandInfo> RowBands { get; set; } = [];
    public List<TableColumnBandInfo> ColBands { get; set; } = [];
}

public sealed class TableRowBandInfo
{
    public int RowIndex { get; set; }
    public string Type { get; set; } = "data";
    public BboxInfo Bbox { get; set; } = new();
}

public sealed class TableColumnBandInfo
{
    public int ColIndex { get; set; }
    public BboxInfo Bbox { get; set; } = new();
}

public sealed class TableHeaderInfo
{
    public int RowIndex { get; set; }
    public List<TableHeaderColumnInfo> Columns { get; set; } = [];
    public List<TableHeaderCellInfo> Cells { get; set; } = [];
}

public sealed class TableHeaderColumnInfo
{
    public int ColIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public BboxInfo Bbox { get; set; } = new();
    public double Confidence { get; set; }
}

public sealed class TableHeaderCellInfo
{
    public int RowIndex { get; set; }
    public int ColIndex { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public BboxInfo Bbox { get; set; } = new();
    public List<string> TokenIds { get; set; } = [];
}

public sealed class TableCellInfo
{
    public int RowIndex { get; set; }
    public int ColIndex { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
    public string Text { get; set; } = string.Empty;
    public TableCellNormalizedInfo Normalized { get; set; } = new();
    public double Confidence { get; set; }
    public BboxInfo Bbox { get; set; } = new();
    public List<string> TokenIds { get; set; } = [];
    public bool IsLowConfidence { get; set; }
}

public sealed class TableCellNormalizedInfo
{
    public string Type { get; set; } = "string";
    public object? Value { get; set; }
    public string? Currency { get; set; }
    public string? Unit { get; set; }
}

public sealed class TableRowInfo
{
    public int RowIndex { get; set; }
    public string Type { get; set; } = "data";
    public Dictionary<string, object?> Values { get; set; } = [];
    public TableRowSourceInfo Source { get; set; } = new();
    public double Confidence { get; set; }
    public bool IsLowConfidence { get; set; }
}

public sealed class TableRowSourceInfo
{
    public List<TableCellRefInfo> CellRefs { get; set; } = [];
}

public sealed class TableCellRefInfo
{
    public int RowIndex { get; set; }
    public int ColIndex { get; set; }
}

public sealed class TableTokenCoverageInfo
{
    public int TokenCountInCells { get; set; }
    public int TokenCountOverlappingTableBbox { get; set; }
    public double CoverageRatio { get; set; }
}

public sealed class PagePreprocessingInfo
{
    public int PageIndex { get; set; }
    public bool DeskewAttempted { get; set; }
    public bool DeskewApplied { get; set; }
    public double DetectedDegrees { get; set; }
    public double DetectedConfidence { get; set; }
    public double AppliedDegrees { get; set; }
}

public sealed class PageDebugArtifactInfo
{
    public int PageIndex { get; set; }
    public string? OriginalOrRenderPath { get; set; }
    public string? GrayPath { get; set; }
    public string? PreprocessedPath { get; set; }
    public string? TokenOverlayPath { get; set; }
    public string? LineOverlayPath { get; set; }
    public string? BlockOverlayPath { get; set; }
    public string? TableOverlayPath { get; set; }
    public string? RegionOverlayPath { get; set; }
}

public sealed class PageNoiseDiagnosticsInfo
{
    public int PageIndex { get; set; }
    public int TotalTokenCount { get; set; }
    public int LowConfidenceTokenCount { get; set; }
    public int TinyTokenCount { get; set; }
    public int SymbolLikeTokenCount { get; set; }
    public int SuspectedDecorativeNoiseCount { get; set; }
    public double SuspectedDecorativeNoiseRatio { get; set; }
}

public sealed class FieldExtractionDiagnosticsInfo
{
    public int PageIndex { get; set; }
    public int CandidateCount { get; set; }
    public int PromotedFieldCount { get; set; }
    public int AmbiguousCandidateCount { get; set; }
}

public sealed class LineReconstructionDiagnosticsInfo
{
    public int PageIndex { get; set; }
    public int OriginalLineCount { get; set; }
    public int ReconstructedLineCount { get; set; }
    public int TokensAssigned { get; set; }
    public bool Successful { get; set; }
}

public sealed class TokenCleanupStatsInfo
{
    public int PageIndex { get; set; }
    public int TokensOriginal { get; set; }
    public int TokensModified { get; set; }
    public int TokensRemoved { get; set; }
    public int TokensSplit { get; set; }
    public int CheckboxArtifactsRemoved { get; set; }
    public int UnderlineArtifactsRemoved { get; set; }
    public int DictionaryCorrections { get; set; }
    public int TokensCleaned { get; set; }
}

public sealed class StructuredFieldExtractionStatsInfo
{
    public int PagesProcessed { get; set; }
    public int KeyValueCandidateCount { get; set; }
    public int PromotedFieldCount { get; set; }
    public int CheckboxDerivedFieldCount { get; set; }
}

public sealed class PipelineStageTimingInfo
{
    public string StageName { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string Status { get; set; } = "completed";
    public string? Note { get; set; }
}

public sealed class OptionSnapshotInfo
{
    public int TargetDpi { get; set; } = 300;
    public string Language { get; set; } = "eng";
    public int? PageSegMode { get; set; }
    public int? EngineMode { get; set; }
    public bool PreserveInterwordSpaces { get; set; } = true;
    public bool SaveTokenOverlay { get; set; }
    public bool EnableNoiseFiltering { get; set; }
    public bool EnableDeskew { get; set; } = true;
    public double MaxDeskewDegrees { get; set; } = 40.0;
    public double DeskewAngleStep { get; set; } = 0.5;
    public double MinDeskewConfidence { get; set; } = 0.15;
    public bool EnableDenoise { get; set; }
    public string DenoiseMethod { get; set; } = "median";
    public int DenoiseKernel { get; set; } = 3;
    public bool EnableBinarization { get; set; }
    public string BinarizationMethod { get; set; } = "otsu";
    public bool EnableContrastEnhancement { get; set; }
    public string ContrastMethod { get; set; } = "clahe";
    public bool SaveJsonToDisk { get; set; } = true;
    public string? OutputFolder { get; set; }
    public bool SaveDebugArtifacts { get; set; }
    public string ProfileName { get; set; } = "default";
}
