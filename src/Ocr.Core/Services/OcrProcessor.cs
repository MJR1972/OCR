
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Ghostscript.NET.Rasterizer;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;
using Ocr.Core.Models;
using Ocr.Core.Pipeline;
using OpenCvSharp;
using Tesseract;

namespace Ocr.Core.Services;

public sealed class OcrProcessor : IOcrProcessor
{
    private readonly ITableDetector _tableDetector;
    private readonly ILineReconstructor _lineReconstructor;
    private readonly IRegionDetector _regionDetector;
    private readonly ITokenCleanupService _tokenCleanupService;
    private readonly IKeyValueExtractor _keyValueExtractor;
    private readonly IFieldRecognizer _fieldRecognizer;
    private readonly IStructuredFieldExtractor _structuredFieldExtractor;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif", ".bmp"
    };

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Include,
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private const double MeanConfidenceWarningThreshold = 0.72;
    private const int LowConfidenceCountWarningThreshold = 25;
    private const double LargeDeskewAngleWarningThreshold = 3.0;
    private const int VerySmallTokenAreaThreshold = 81;
    private const int VerySmallTokenCountThreshold = 35;
    private const double NoiseWarningRatioThreshold = 0.2;
    private const int FilteredTokenWarningThreshold = 25;
    private static readonly Regex SymbolLikeRegex = new(@"^[\p{P}\p{S}]+$", RegexOptions.Compiled);

    public OcrProcessor()
        : this(new HybridTableDetector(), new GeometryLineReconstructor(), new OpenCvRegionDetector(), new TokenCleanupService(), new HeuristicKeyValueExtractor(), new HeuristicFieldRecognizer(), new StructuredFieldExtractor())
    {
    }

    public OcrProcessor(ITableDetector tableDetector)
        : this(tableDetector, new GeometryLineReconstructor(), new OpenCvRegionDetector(), new TokenCleanupService(), new HeuristicKeyValueExtractor(), new HeuristicFieldRecognizer(), new StructuredFieldExtractor())
    {
    }

    public OcrProcessor(ITableDetector tableDetector, IKeyValueExtractor keyValueExtractor, IFieldRecognizer fieldRecognizer)
        : this(tableDetector, new GeometryLineReconstructor(), new OpenCvRegionDetector(), new TokenCleanupService(), keyValueExtractor, fieldRecognizer, new StructuredFieldExtractor())
    {
    }

    public OcrProcessor(ITableDetector tableDetector, IRegionDetector regionDetector, IKeyValueExtractor keyValueExtractor, IFieldRecognizer fieldRecognizer)
        : this(tableDetector, new GeometryLineReconstructor(), regionDetector, new TokenCleanupService(), keyValueExtractor, fieldRecognizer, new StructuredFieldExtractor())
    {
    }

    public OcrProcessor(ITableDetector tableDetector, ILineReconstructor lineReconstructor, IRegionDetector regionDetector, ITokenCleanupService tokenCleanupService, IKeyValueExtractor keyValueExtractor, IFieldRecognizer fieldRecognizer)
        : this(tableDetector, lineReconstructor, regionDetector, tokenCleanupService, keyValueExtractor, fieldRecognizer, new StructuredFieldExtractor())
    {
    }

    public OcrProcessor(
        ITableDetector tableDetector,
        ILineReconstructor lineReconstructor,
        IRegionDetector regionDetector,
        ITokenCleanupService tokenCleanupService,
        IKeyValueExtractor keyValueExtractor,
        IFieldRecognizer fieldRecognizer,
        IStructuredFieldExtractor structuredFieldExtractor)
    {
        _tableDetector = tableDetector;
        _lineReconstructor = lineReconstructor;
        _regionDetector = regionDetector;
        _tokenCleanupService = tokenCleanupService;
        _keyValueExtractor = keyValueExtractor;
        _fieldRecognizer = fieldRecognizer;
        _structuredFieldExtractor = structuredFieldExtractor;
    }

    public OcrResult ProcessFile(string filePath, OcrOptions? options = null, CancellationToken ct = default)
    {
        var runStopwatch = Stopwatch.StartNew();
        options ??= new OcrOptions();
        var effectiveOptions = NormalizeOptions(options);

        var extension = Path.GetExtension(filePath ?? string.Empty);
        var fileType = DetermineFileType(extension);
        var mimeType = DetermineMimeType(extension);
        var root = BuildContract(filePath ?? string.Empty, effectiveOptions, fileType, mimeType);
        var pipelineContext = new OcrPipelineContext(filePath ?? string.Empty, effectiveOptions, fileType, mimeType, root);
        IOcrPipelineRunner pipelineRunner = new OcrPipelineRunner();

        try
        {
            pipelineRunner.ExecuteStage(pipelineContext, OcrPipelineStageNames.InputLoad, () =>
            {
                if (ct.IsCancellationRequested)
                {
                    AddError(root, "operation_cancelled", "OCR request was cancelled.");
                    return;
                }

                foreach (var error in Validate(filePath ?? string.Empty))
                {
                    root.Errors.Add(error);
                }

                pipelineContext.TessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
                foreach (var error in ValidateTessdata(pipelineContext.TessdataPath, effectiveOptions.Language))
                {
                    root.Errors.Add(error);
                }

                pipelineContext.RunOutputFolder = PrepareRunOutputFolder(filePath, effectiveOptions);
            });

            if (root.Errors.Count > 0)
            {
                root.Document.Source.PageCount = 0;
                return FinalizeResult(root, filePath, effectiveOptions, runStopwatch.ElapsedMilliseconds, pipelineContext.RunOutputFolder);
            }

            var totalRenderMs = 0;
            var pageImages = new List<LoadedPage>();
            pipelineRunner.ExecuteStage(pipelineContext, OcrPipelineStageNames.Render, () =>
            {
                pageImages = LoadPages(filePath ?? string.Empty, fileType, effectiveOptions, pipelineContext.RunOutputFolder, ct, root, out totalRenderMs);
                pipelineContext.LoadedPageCount = pageImages.Count;
                pipelineContext.Items["pageImages"] = pageImages;
            });
            root.Document.Source.PageCount = pageImages.Count;

            if (pageImages.Count == 0)
            {
                AddError(root, "no_pages_rendered", "No pages were loaded from the input file.");
                return FinalizeResult(root, filePath, effectiveOptions, runStopwatch.ElapsedMilliseconds, pipelineContext.RunOutputFolder);
            }

            var languages = SplitLanguages(effectiveOptions.Language);
            var engineMode = ResolveEngineMode(effectiveOptions.EngineMode, root);
            var pageSegMode = ResolvePageSegMode(effectiveOptions.PageSegMode, root);
            using var engine = new TesseractEngine(pipelineContext.TessdataPath, string.Join('+', languages), engineMode);
            engine.DefaultPageSegMode = pageSegMode;
            engine.SetVariable("preserve_interword_spaces", effectiveOptions.PreserveInterwordSpaces ? "1" : "0");
            if (effectiveOptions.PreserveInterwordSpaces)
            {
                engine.SetVariable("tessedit_fix_fuzzy_spaces", "0");
            }

            var pageResults = new List<PageInfo>(pageImages.Count);
            var pageTimings = new List<int>(pageImages.Count);
            var preprocessProfiles = new List<PagePreprocessingInfo>(pageImages.Count);
            var debugArtifactPaths = new List<PageDebugArtifactInfo>();
            var pageNoiseDiagnostics = new List<PageNoiseDiagnosticsInfo>(pageImages.Count);
            var lineReconstructionDiagnostics = new List<LineReconstructionDiagnosticsInfo>(pageImages.Count);
            var tokenCleanupStats = new List<TokenCleanupStatsInfo>(pageImages.Count);
            var fieldExtractionDiagnostics = new List<FieldExtractionDiagnosticsInfo>(pageImages.Count);
            var filteredTokenIds = new List<string>();
            var documentWordMap = new Dictionary<string, string>(StringComparer.Ordinal);

            var preprocessTotal = 0;
            var ocrTotal = 0;
            var layoutTotal = 0;
            var tableDetectTotal = 0;
            var postprocessTotal = 0;
            var recognitionTotal = 0;

            foreach (var pageImage in pageImages)
            {
                if (ct.IsCancellationRequested)
                {
                    AddError(root, "operation_cancelled", "OCR request was cancelled.");
                    break;
                }

                using var sourceMat = pageImage.Image;

                PreprocessResult preprocessResult = default!;
                var preprocessSw = Stopwatch.StartNew();
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.Preprocess} [Page {pageImage.PageIndex}]",
                    () => preprocessResult = PreprocessPage(sourceMat, pageImage.PageIndex, effectiveOptions, pipelineContext.RunOutputFolder));
                preprocessSw.Stop();

                preprocessProfiles.Add(new PagePreprocessingInfo
                {
                    PageIndex = pageImage.PageIndex,
                    DeskewAttempted = preprocessResult.DeskewAttempted,
                    DeskewApplied = preprocessResult.DeskewApplied,
                    DetectedDegrees = preprocessResult.CandidateDegrees,
                    DetectedConfidence = preprocessResult.CandidateConfidence,
                    AppliedDegrees = preprocessResult.AppliedDegrees
                });

                List<WordCandidate> words = [];
                var ocrSw = Stopwatch.StartNew();
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.OcrExtraction} [Page {pageImage.PageIndex}]",
                    () => words = RunOcr(engine, preprocessResult.ProcessedMat));
                ocrSw.Stop();

                LayoutResult layout = default!;
                var layoutSw = Stopwatch.StartNew();
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.LayoutAnalysis} [Page {pageImage.PageIndex}]",
                    () => layout = BuildLayout(
                        words,
                        pageImage.PageIndex,
                        root.Definitions.Confidence,
                        preprocessResult.ProcessedMat.Width,
                        preprocessResult.ProcessedMat.Height,
                        _lineReconstructor,
                        _tokenCleanupService,
                        lineReconstructionDiagnostics));
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.TokenCleanup} [Page {pageImage.PageIndex}]",
                    () => { },
                    note: "Token cleanup is executed inside the layout analysis stage.");
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.LineReconstruction} [Page {pageImage.PageIndex}]",
                    () => { },
                    note: "Line reconstruction is executed inside the layout analysis stage.");
                tokenCleanupStats.Add(new TokenCleanupStatsInfo
                {
                    PageIndex = pageImage.PageIndex,
                    TokensOriginal = layout.TokenCleanupStats.TokensOriginal,
                    TokensModified = layout.TokenCleanupStats.TokensModified,
                    TokensRemoved = layout.TokenCleanupStats.TokensRemoved,
                    TokensSplit = layout.TokenCleanupStats.TokensSplit,
                    CheckboxArtifactsRemoved = layout.TokenCleanupStats.CheckboxArtifactsRemoved,
                    UnderlineArtifactsRemoved = layout.TokenCleanupStats.UnderlineArtifactsRemoved,
                    DictionaryCorrections = layout.TokenCleanupStats.DictionaryCorrections,
                    TokensCleaned = Math.Max(0, layout.TokenCleanupStats.TokensOriginal - layout.TokenCleanupStats.TokensRemoved)
                });
                var noiseAnalysis = AnalyzePageNoise(layout);
                var filteredThisPage = 0;
                if (effectiveOptions.EnableNoiseFiltering)
                {
                    var filtered = ApplyConservativeNoiseFiltering(layout, noiseAnalysis);
                    if (filtered.Count > 0)
                    {
                        filteredThisPage = filtered.Count;
                        filteredTokenIds.AddRange(filtered);
                        noiseAnalysis = AnalyzePageNoise(layout);
                    }
                }
                layoutSw.Stop();
                var processedWidth = preprocessResult.ProcessedMat.Width;
                var processedHeight = preprocessResult.ProcessedMat.Height;

                var pageTiming = new PageTimingInfo
                {
                    RenderMs = pageImage.RenderMs,
                    PreprocessMs = (int)preprocessSw.ElapsedMilliseconds,
                    OcrMs = (int)ocrSw.ElapsedMilliseconds,
                    LayoutMs = (int)layoutSw.ElapsedMilliseconds,
                    TableDetectMs = 0,
                    PostprocessMs = 0
                };

                TableDetectionResult tableDetection = new();
                var tableSw = Stopwatch.StartNew();
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.TableDetection} [Page {pageImage.PageIndex}]",
                    () => tableDetection = _tableDetector.Detect(new PageInfo
                    {
                        PageIndex = pageImage.PageIndex,
                        Size = new PageSizeInfo
                        {
                            WidthPx = processedWidth,
                            HeightPx = processedHeight,
                            Dpi = effectiveOptions.TargetDpi
                        },
                        Tokens = layout.Tokens,
                        Lines = layout.Lines,
                        Blocks = layout.Blocks,
                        Quality = new PageQualityInfo
                        {
                            MeanTokenConfidence = layout.Tokens.Count == 0 ? 0 : layout.Tokens.Average(t => t.Confidence),
                            LowConfidenceTokenCount = layout.Tokens.Count(t => t.IsLowConfidence),
                            BlankPage = string.IsNullOrWhiteSpace(layout.FullText)
                        }
                    }, preprocessResult.ProcessedMat));
                tableSw.Stop();
                pageTiming.TableDetectMs = (int)tableSw.ElapsedMilliseconds;

                RegionDetectionResult regionDetection = new();
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.RegionDetection} [Page {pageImage.PageIndex}]",
                    () => regionDetection = _regionDetector.Detect(new PageInfo
                    {
                        PageIndex = pageImage.PageIndex,
                        Tokens = layout.Tokens,
                        Lines = layout.Lines,
                        Blocks = layout.Blocks,
                        Size = new PageSizeInfo
                        {
                            WidthPx = processedWidth,
                            HeightPx = processedHeight,
                            Dpi = effectiveOptions.TargetDpi
                        }
                    }, preprocessResult.ProcessedMat));

                var pageInfo = new PageInfo
                {
                    PageIndex = pageImage.PageIndex,
                    PageId = $"page-{pageImage.PageIndex}",
                    Size = new PageSizeInfo
                    {
                        WidthPx = processedWidth,
                        HeightPx = processedHeight,
                        Dpi = effectiveOptions.TargetDpi,
                        RotationDegrees = preprocessResult.AppliedDegrees
                    },
                    Timing = pageTiming,
                    Quality = new PageQualityInfo
                    {
                        MeanTokenConfidence = layout.Tokens.Count == 0 ? 0 : layout.Tokens.Average(t => t.Confidence),
                        LowConfidenceTokenCount = layout.Tokens.Count(t => t.IsLowConfidence),
                        BlankPage = string.IsNullOrWhiteSpace(layout.FullText)
                    },
                    Text = new PageTextInfo
                    {
                        FullText = layout.FullText,
                        ReadingOrder = root.Definitions.ReadingOrder.Default
                    },
                    Tokens = layout.Tokens,
                    Lines = layout.Lines,
                    Blocks = layout.Blocks,
                    Tables = tableDetection.Tables,
                    Regions = regionDetection.Regions,
                    PageWords = BuildUniqueWordList(layout.Tokens),
                    Artifacts = new ArtifactsInfo
                    {
                        PageImageRef = preprocessResult.FinalImagePath ?? pageImage.OriginalImagePath,
                        DebugOverlayRef = null
                    }
                };
                MergeWordsIntoDocumentMap(pageInfo.PageWords, documentWordMap);

                KeyValueExtractionResult keyValueExtraction = new();
                var keyValueSw = Stopwatch.StartNew();
                pipelineRunner.ExecuteStage(
                    pipelineContext,
                    $"{OcrPipelineStageNames.StructuredFieldExtraction} [Page {pageImage.PageIndex}]",
                    () => keyValueExtraction = _keyValueExtractor.Extract(pageInfo));
                keyValueSw.Stop();
                pageTiming.PostprocessMs += (int)keyValueSw.ElapsedMilliseconds;
                pageInfo.KeyValueCandidates = keyValueExtraction.Candidates;
                fieldExtractionDiagnostics.Add(keyValueExtraction.Diagnostics);
                foreach (var warning in keyValueExtraction.Warnings)
                {
                    root.Warnings.Add(warning);
                }

                var artifactEntry = new PageDebugArtifactInfo
                {
                    PageIndex = pageImage.PageIndex,
                    OriginalOrRenderPath = pageImage.OriginalImagePath,
                    GrayPath = preprocessResult.GrayImagePath,
                    PreprocessedPath = preprocessResult.FinalImagePath
                };

                if (effectiveOptions.SaveTokenOverlay && !string.IsNullOrWhiteSpace(pipelineContext.RunOutputFolder))
                {
                    var overlays = SaveOverlayArtifacts(preprocessResult.ProcessedMat, layout, pageImage.PageIndex, pipelineContext.RunOutputFolder);
                    pageInfo.Artifacts.DebugOverlayRef = overlays.TokenOverlayPath;
                    artifactEntry.TokenOverlayPath = overlays.TokenOverlayPath;
                    artifactEntry.LineOverlayPath = overlays.LineOverlayPath;
                    artifactEntry.BlockOverlayPath = overlays.BlockOverlayPath;
                }

                if (effectiveOptions.SaveDebugArtifacts && !string.IsNullOrWhiteSpace(pipelineContext.RunOutputFolder) && tableDetection.Overlays.Count > 0)
                {
                    var tableOverlayPath = SaveTableOverlay(preprocessResult.ProcessedMat, tableDetection.Overlays, pageImage.PageIndex, pipelineContext.RunOutputFolder);
                    artifactEntry.TableOverlayPath = tableOverlayPath;
                }

                if (effectiveOptions.SaveDebugArtifacts && !string.IsNullOrWhiteSpace(pipelineContext.RunOutputFolder) && regionDetection.Overlays.Count > 0)
                {
                    var regionOverlayPath = SaveRegionOverlay(preprocessResult.ProcessedMat, regionDetection.Overlays, pageImage.PageIndex, pipelineContext.RunOutputFolder);
                    artifactEntry.RegionOverlayPath = regionOverlayPath;
                }

                if (!string.IsNullOrWhiteSpace(artifactEntry.OriginalOrRenderPath) ||
                    !string.IsNullOrWhiteSpace(artifactEntry.GrayPath) ||
                    !string.IsNullOrWhiteSpace(artifactEntry.PreprocessedPath) ||
                    !string.IsNullOrWhiteSpace(artifactEntry.TokenOverlayPath) ||
                    !string.IsNullOrWhiteSpace(artifactEntry.LineOverlayPath) ||
                    !string.IsNullOrWhiteSpace(artifactEntry.BlockOverlayPath) ||
                    !string.IsNullOrWhiteSpace(artifactEntry.TableOverlayPath) ||
                    !string.IsNullOrWhiteSpace(artifactEntry.RegionOverlayPath))
                {
                    debugArtifactPaths.Add(artifactEntry);
                }

                AddQualityWarnings(root, pageInfo, preprocessResult);
                AddNoiseWarnings(root, pageInfo, noiseAnalysis, effectiveOptions.EnableNoiseFiltering, filteredThisPage);
                AddRegionWarnings(root, pageInfo, regionDetection.Diagnostics);
                AddTokenCleanupInfo(root, pageInfo.PageIndex, layout.TokenCleanupStats);
                preprocessResult.ProcessedMat.Dispose();

                pageNoiseDiagnostics.Add(new PageNoiseDiagnosticsInfo
                {
                    PageIndex = pageInfo.PageIndex,
                    TotalTokenCount = noiseAnalysis.TotalTokenCount,
                    LowConfidenceTokenCount = noiseAnalysis.LowConfidenceTokenCount,
                    TinyTokenCount = noiseAnalysis.TinyTokenCount,
                    SymbolLikeTokenCount = noiseAnalysis.SymbolLikeTokenCount,
                    SuspectedDecorativeNoiseCount = noiseAnalysis.SuspectedDecorativeNoiseCount,
                    SuspectedDecorativeNoiseRatio = noiseAnalysis.SuspectedDecorativeNoiseRatio
                });

                pageResults.Add(pageInfo);

                var totalPageMs = pageTiming.RenderMs + pageTiming.PreprocessMs + pageTiming.OcrMs + pageTiming.LayoutMs + pageTiming.PostprocessMs;
                pageTimings.Add(totalPageMs);

                preprocessTotal += pageTiming.PreprocessMs;
                ocrTotal += pageTiming.OcrMs;
                layoutTotal += pageTiming.LayoutMs;
                tableDetectTotal += pageTiming.TableDetectMs;
                postprocessTotal += pageTiming.PostprocessMs;
            }

            var recognitionSw = Stopwatch.StartNew();
            FieldRecognitionResult fieldRecognition = new();
            pipelineRunner.ExecuteStage(
                pipelineContext,
                OcrPipelineStageNames.StructuredFieldExtraction,
                () => fieldRecognition = _fieldRecognizer.Recognize(pageResults, root.Definitions.Confidence.LowFieldThreshold));

            StructuredFieldExtractionResult structuredExtraction = new();
            pipelineRunner.ExecuteStage(
                pipelineContext,
                $"{OcrPipelineStageNames.StructuredFieldExtraction} (Additive)",
                () => structuredExtraction = _structuredFieldExtractor.Extract(
                    pageResults,
                    fieldRecognition.Fields,
                    root.Definitions.Confidence.LowFieldThreshold),
                preserveOnFailure: true,
                onFailure: ex => AddWarning(
                    root,
                    "structured_extraction_stage_failed",
                    $"Structured extraction stage failed; preserving raw OCR evidence. {ex.Message}"));
            foreach (var page in pageResults)
            {
                if (!structuredExtraction.AdditionalKeyValueCandidatesByPage.TryGetValue(page.PageIndex, out var additions) ||
                    additions.Count == 0)
                {
                    continue;
                }

                page.KeyValueCandidates.AddRange(additions);
                RenumberKeyValueCandidates(page);
            }

            var mergedFields = MergeRecognitionFields(
                fieldRecognition.Fields,
                structuredExtraction.AdditionalFields,
                root.Definitions.Confidence.LowFieldThreshold);
            recognitionSw.Stop();
            recognitionTotal = (int)recognitionSw.ElapsedMilliseconds;
            root.Recognition.Fields = mergedFields;
            foreach (var warning in fieldRecognition.Warnings)
            {
                root.Warnings.Add(warning);
            }
            foreach (var warning in structuredExtraction.Warnings)
            {
                root.Warnings.Add(warning);
            }

            foreach (var diag in fieldExtractionDiagnostics)
            {
                var basePromoted = fieldRecognition.PromotedByPage.GetValueOrDefault(diag.PageIndex);
                var structuredPromoted = structuredExtraction.AdditionalFields.Count(f => f.Source.PageIndex == diag.PageIndex);
                var structuredCandidates = structuredExtraction.AdditionalKeyValueCandidatesByPage.GetValueOrDefault(diag.PageIndex)?.Count ?? 0;
                diag.CandidateCount += structuredCandidates;
                diag.PromotedFieldCount = basePromoted + structuredPromoted;
            }

            root.Pages.Clear();
            root.Pages.AddRange(pageResults);
            pipelineContext.Items["pages"] = pageResults;
            pipelineContext.Items["pageCount"] = pageResults.Count;
            pipelineContext.Items["warningsCount"] = root.Warnings.Count;
            pipelineContext.Items["errorsCount"] = root.Errors.Count;

            root.Extensions.PagePreprocessing = preprocessProfiles;
            root.Extensions.DebugArtifactPaths = debugArtifactPaths;
            root.Extensions.PageNoiseDiagnostics = pageNoiseDiagnostics;
            root.Extensions.LineReconstructionDiagnostics = lineReconstructionDiagnostics;
            root.Extensions.TokenCleanupStats = tokenCleanupStats;
            root.Extensions.FilteredTokenIds = filteredTokenIds;
            root.Extensions.FieldExtractionDiagnostics = fieldExtractionDiagnostics;
            root.Extensions.StructuredFieldExtractionStats = new StructuredFieldExtractionStatsInfo
            {
                PagesProcessed = pageResults.Count,
                KeyValueCandidateCount = structuredExtraction.KeyValueCandidateCount,
                PromotedFieldCount = structuredExtraction.PromotedFieldCount,
                CheckboxDerivedFieldCount = structuredExtraction.CheckboxDerivedFieldCount
            };
            root.Extensions.PipelineStageTimings = pipelineContext.StageTimings
                .Select(t => new PipelineStageTimingInfo
                {
                    StageName = t.StageName,
                    DurationMs = t.DurationMs,
                    Status = t.Status,
                    Note = t.Note
                })
                .ToList();
            root.DocumentWords = documentWordMap.Values
                .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var anyDeskewApplied = preprocessProfiles.Any(p => p.DeskewApplied);
            var deskewDegrees = anyDeskewApplied ? preprocessProfiles.Where(p => p.DeskewApplied).Average(p => p.AppliedDegrees) : 0;
            var deskewConfidence = anyDeskewApplied
                ? preprocessProfiles.Where(p => p.DeskewApplied).Average(p => p.DetectedConfidence)
                : 0;

            root.Document.Processing.Preprocessing.Deskew = new DeskewInfo
            {
                Attempted = effectiveOptions.EnableDeskew,
                Applied = anyDeskewApplied,
                Degrees = deskewDegrees,
                Confidence = deskewConfidence
            };

            root.Metrics = new MetricsInfo
            {
                TotalMs = (int)runStopwatch.ElapsedMilliseconds,
                DocumentOcrMs = ocrTotal,
                PagesMs = pageTimings,
                BreakdownMs = new BreakdownInfo
                {
                    RenderMs = totalRenderMs,
                    PreprocessMs = preprocessTotal,
                    OcrMs = ocrTotal,
                    LayoutMs = layoutTotal,
                    TableDetectMs = tableDetectTotal,
                    RecognitionMs = recognitionTotal,
                    PostprocessMs = postprocessTotal
                }
            };

            pipelineRunner.ExecuteStage(
                pipelineContext,
                OcrPipelineStageNames.FinalAssembly,
                () => { },
                note: "Final result serialization and output path handling.");
            return FinalizeResult(root, filePath, effectiveOptions, runStopwatch.ElapsedMilliseconds, pipelineContext.RunOutputFolder);
        }
        catch (Exception ex)
        {
            AddError(root, "unexpected_exception", ex.Message);
            root.Metrics.TotalMs = (int)runStopwatch.ElapsedMilliseconds;
            return FinalizeResult(root, filePath, effectiveOptions, runStopwatch.ElapsedMilliseconds, null);
        }
    }

    private static OcrResult FinalizeResult(OcrContractRoot root, string? filePath, OcrOptions options, long totalMs, string? runOutputFolder)
    {
        root.Metrics.TotalMs = (int)totalMs;

        string? outputJsonPath = null;
        var json = JsonConvert.SerializeObject(root, SerializerSettings);

        if (options.SaveJsonToDisk)
        {
            try
            {
                outputJsonPath = SaveJson(json, filePath, options.OutputFolder, runOutputFolder);
            }
            catch (Exception ex)
            {
                AddError(root, "output_write_failed", ex.Message);
                json = JsonConvert.SerializeObject(root, SerializerSettings);
            }
        }

        return new OcrResult
        {
            Json = json,
            OutputJsonPath = outputJsonPath
        };
    }

    private static OcrOptions NormalizeOptions(OcrOptions options)
    {
        var maxDeskew = Math.Clamp(options.MaxDeskewDegrees, 0, 40.0);
        var step = options.DeskewAngleStep <= 0 ? 0.5 : options.DeskewAngleStep;
        var minConfidence = Math.Clamp(options.MinDeskewConfidence, 0, 1);

        return options with
        {
            TargetDpi = options.TargetDpi <= 0 ? 300 : options.TargetDpi,
            Language = string.IsNullOrWhiteSpace(options.Language) ? "eng" : options.Language,
            MaxDeskewDegrees = maxDeskew,
            DeskewAngleStep = step,
            MinDeskewConfidence = minConfidence,
            DenoiseKernel = Math.Max(1, options.DenoiseKernel | 1),
            ProfileName = string.IsNullOrWhiteSpace(options.ProfileName) ? "default" : options.ProfileName
        };
    }

    private static List<IssueInfo> Validate(string filePath)
    {
        var errors = new List<IssueInfo>();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            errors.Add(new IssueInfo
            {
                Code = "invalid_file_path",
                Severity = "error",
                Message = "filePath is required.",
                PageIndex = null
            });

            return errors;
        }

        if (!File.Exists(filePath))
        {
            errors.Add(new IssueInfo
            {
                Code = "file_not_found",
                Severity = "error",
                Message = $"File not found: {filePath}",
                PageIndex = null
            });

            return errors;
        }

        var extension = Path.GetExtension(filePath);
        if (!SupportedExtensions.Contains(extension))
        {
            errors.Add(new IssueInfo
            {
                Code = "unsupported_extension",
                Severity = "error",
                Message = $"Unsupported extension: {extension}",
                PageIndex = null,
                Details = new Dictionary<string, object?>
                {
                    ["supportedExtensions"] = SupportedExtensions.OrderBy(x => x).ToArray()
                }
            });
        }

        return errors;
    }

    private static IEnumerable<IssueInfo> ValidateTessdata(string tessdataPath, string language)
    {
        if (!Directory.Exists(tessdataPath))
        {
            yield return new IssueInfo
            {
                Code = "missing_tessdata",
                Severity = "error",
                Message = $"Tessdata folder not found. Expected: {tessdataPath}",
                PageIndex = null
            };
            yield break;
        }

        foreach (var lang in SplitLanguages(language))
        {
            var trainedDataPath = Path.Combine(tessdataPath, $"{lang}.traineddata");
            if (!File.Exists(trainedDataPath))
            {
                yield return new IssueInfo
                {
                    Code = "missing_traineddata",
                    Severity = "error",
                    Message = $"Language file not found: {trainedDataPath}",
                    PageIndex = null
                };
            }
        }
    }

    private static List<string> SplitLanguages(string language)
    {
        return language
            .Split(['+', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<LoadedPage> LoadPages(
        string filePath,
        string fileType,
        OcrOptions options,
        string? runOutputFolder,
        CancellationToken ct,
        OcrContractRoot root,
        out int totalRenderMs)
    {
        totalRenderMs = 0;

        if (string.Equals(fileType, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            return LoadPdfPages(filePath, options.TargetDpi, options.SaveDebugArtifacts, runOutputFolder, ct, root, out totalRenderMs);
        }

        using var image = Cv2.ImRead(filePath, ImreadModes.Color);
        if (image.Empty())
        {
            return [];
        }

        string? originalPath = null;
        if (options.SaveDebugArtifacts && !string.IsNullOrWhiteSpace(runOutputFolder))
        {
            originalPath = Path.Combine(runOutputFolder, "page_1_original_or_render.png");
            Cv2.ImWrite(originalPath, image);
        }

        return
        [
            new LoadedPage
            {
                PageIndex = 1,
                Image = image.Clone(),
                RenderMs = 0,
                OriginalImagePath = originalPath
            }
        ];
    }

    private static List<LoadedPage> LoadPdfPages(
        string filePath,
        int dpi,
        bool saveDebugArtifacts,
        string? runOutputFolder,
        CancellationToken ct,
        OcrContractRoot root,
        out int totalRenderMs)
    {
        var pages = new List<LoadedPage>();
        totalRenderMs = 0;

        try
        {
            using var rasterizer = new GhostscriptRasterizer();
            rasterizer.Open(filePath);

            int pageCount;
            try
            {
                pageCount = rasterizer.PageCount;
            }
            catch (Exception ex)
            {
                AddError(
                    root,
                    "pdf_page_count_failed",
                    $"Failed to determine PDF page count for '{filePath}'. {ex.Message}");
                root.Errors[^1].Details = CreateExceptionDetails(ex);
                return pages;
            }

            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    using var rasterImage = rasterizer.GetPage(dpi, pageNumber);
                    var pngBytes = RasterImageToPngBytes(rasterImage);
                    using var mat = Cv2.ImDecode(pngBytes, ImreadModes.Color);
                    sw.Stop();

                    if (mat.Empty())
                    {
                        AddError(root, "pdf_render_failed", $"Ghostscript rendered an empty page image for page {pageNumber}.", pageNumber);
                        continue;
                    }

                    string? artifactPath = null;
                    if (saveDebugArtifacts && !string.IsNullOrWhiteSpace(runOutputFolder))
                    {
                        artifactPath = Path.Combine(runOutputFolder, $"page_{pageNumber}_original_or_render.png");
                        File.WriteAllBytes(artifactPath, pngBytes);
                    }

                    var renderMs = (int)sw.ElapsedMilliseconds;
                    totalRenderMs += renderMs;

                    pages.Add(new LoadedPage
                    {
                        PageIndex = pageNumber,
                        Image = mat.Clone(),
                        RenderMs = renderMs,
                        OriginalImagePath = artifactPath
                    });
                }
                catch (Exception ex)
                {
                    AddError(root, "pdf_render_failed", $"Failed to render PDF page {pageNumber}. {ex.Message}", pageNumber);
                    root.Errors[^1].Details = CreateExceptionDetails(ex);
                }
            }
        }
        catch (Exception ex)
        {
            var errorCode = ex is DllNotFoundException or TypeLoadException or FileNotFoundException
                ? "ghostscript_not_found"
                : "pdf_render_failed";
            AddError(root, errorCode, $"Failed to initialize Ghostscript rasterizer. {ex.Message}");
            root.Errors[^1].Details = CreateExceptionDetails(ex);
        }

        return pages;
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

        var fromBitmap = skImageType.GetMethod("FromBitmap", BindingFlags.Public | BindingFlags.Static, binder: null, types: [bitmapType], modifiers: null)
            ?? throw new InvalidOperationException("SkiaSharp.SKImage.FromBitmap was not found.");
        var encode = skImageType.GetMethod("Encode", BindingFlags.Public | BindingFlags.Instance, binder: null, types: [skEncodedImageFormatType, typeof(int)], modifiers: null)
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

    private static PreprocessResult PreprocessPage(Mat source, int pageIndex, OcrOptions options, string? runOutputFolder)
    {
        var gray = new Mat();
        if (source.Channels() == 1)
        {
            source.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        }

        string? grayPath = null;
        if (options.SaveDebugArtifacts && !string.IsNullOrWhiteSpace(runOutputFolder))
        {
            grayPath = Path.Combine(runOutputFolder, $"page_{pageIndex}_gray.png");
            Cv2.ImWrite(grayPath, gray);
        }

        var processed = gray.Clone();
        var deskewAttempted = options.EnableDeskew;
        var deskewApplied = false;
        var candidateDegrees = 0.0;
        var candidateConfidence = 0.0;
        var appliedDegrees = 0.0;

        if (options.EnableDeskew)
        {
            var estimation = EstimateDeskew(gray, options.MaxDeskewDegrees, options.DeskewAngleStep);
            candidateDegrees = estimation.Angle;
            candidateConfidence = estimation.Confidence;

            if (candidateConfidence >= options.MinDeskewConfidence && Math.Abs(candidateDegrees) > double.Epsilon)
            {
                processed.Dispose();
                processed = RotateKeepAll(gray, -candidateDegrees);
                deskewApplied = true;
                appliedDegrees = candidateDegrees;
            }
        }

        if (options.EnableDenoise)
        {
            ApplyDenoise(processed, options.DenoiseMethod, options.DenoiseKernel);
        }

        if (options.EnableBinarization)
        {
            ApplyBinarization(processed, options.BinarizationMethod);
        }

        if (options.EnableContrastEnhancement)
        {
            ApplyContrast(processed, options.ContrastMethod);
        }

        string? finalPath = null;
        if (options.SaveDebugArtifacts && !string.IsNullOrWhiteSpace(runOutputFolder))
        {
            finalPath = Path.Combine(runOutputFolder, $"page_{pageIndex}_preprocessed.png");
            Cv2.ImWrite(finalPath, processed);
        }

        return new PreprocessResult
        {
            ProcessedMat = processed,
            DeskewAttempted = deskewAttempted,
            DeskewApplied = deskewApplied,
            CandidateDegrees = candidateDegrees,
            CandidateConfidence = candidateConfidence,
            AppliedDegrees = deskewApplied ? appliedDegrees : 0,
            GrayImagePath = grayPath,
            FinalImagePath = finalPath
        };
    }

    private static void ApplyDenoise(Mat image, string method, int kernel)
    {
        var k = Math.Max(1, kernel | 1);
        switch (method?.Trim().ToLowerInvariant())
        {
            case "gaussian":
                Cv2.GaussianBlur(image, image, new OpenCvSharp.Size(k, k), 0);
                break;
            default:
                Cv2.MedianBlur(image, image, k);
                break;
        }
    }

    private static void ApplyBinarization(Mat image, string method)
    {
        switch (method?.Trim().ToLowerInvariant())
        {
            case "adaptive":
                Cv2.AdaptiveThreshold(image, image, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 10);
                break;
            default:
                Cv2.Threshold(image, image, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                break;
        }
    }

    private static void ApplyContrast(Mat image, string method)
    {
        switch (method?.Trim().ToLowerInvariant())
        {
            case "equalize":
                Cv2.EqualizeHist(image, image);
                break;
            default:
                using (var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8)))
                {
                    clahe.Apply(image, image);
                }
                break;
        }
    }

    private static DeskewEstimation EstimateDeskew(Mat gray, double maxDegrees, double step)
    {
        if (maxDegrees <= 0)
        {
            return new DeskewEstimation(0, 0);
        }

        var safeStep = Math.Max(0.1, step);

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var bestScore = double.MinValue;
        var secondBest = double.MinValue;
        var bestAngle = 0.0;

        for (var angle = -maxDegrees; angle <= maxDegrees + 0.0001; angle += safeStep)
        {
            using var rotated = RotateKeepAll(binary, angle);
            var score = ProjectionVarianceScore(rotated);

            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && Math.Abs(angle) < Math.Abs(bestAngle)))
            {
                secondBest = bestScore;
                bestScore = score;
                bestAngle = angle;
            }
            else if (score > secondBest)
            {
                secondBest = score;
            }
        }

        if (bestScore <= 0)
        {
            return new DeskewEstimation(0, 0);
        }

        if (secondBest < 0)
        {
            secondBest = 0;
        }

        var confidence = Math.Clamp((bestScore - secondBest) / bestScore, 0, 1);
        return new DeskewEstimation(bestAngle, confidence);
    }

    private static double ProjectionVarianceScore(Mat binary)
    {
        if (binary.Rows == 0)
        {
            return 0;
        }

        double sum = 0;
        double sumSq = 0;

        for (var row = 0; row < binary.Rows; row++)
        {
            using var rowMat = binary.Row(row);
            var count = Cv2.CountNonZero(rowMat);
            sum += count;
            sumSq += (double)count * count;
        }

        var mean = sum / binary.Rows;
        return (sumSq / binary.Rows) - (mean * mean);
    }

    private static Mat RotateKeepAll(Mat processed, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < double.Epsilon)
        {
            return processed.Clone();
        }

        var center = new Point2f(processed.Width / 2f, processed.Height / 2f);
        using var rotationMatrix = Cv2.GetRotationMatrix2D(center, angleDegrees, 1.0);

        var absCos = Math.Abs(rotationMatrix.At<double>(0, 0));
        var absSin = Math.Abs(rotationMatrix.At<double>(0, 1));

        var boundW = (int)Math.Round(processed.Height * absSin + processed.Width * absCos);
        var boundH = (int)Math.Round(processed.Height * absCos + processed.Width * absSin);

        rotationMatrix.Set(0, 2, rotationMatrix.At<double>(0, 2) + boundW / 2.0 - center.X);
        rotationMatrix.Set(1, 2, rotationMatrix.At<double>(1, 2) + boundH / 2.0 - center.Y);

        var rotated = new Mat();
        Cv2.WarpAffine(
            processed,
            rotated,
            rotationMatrix,
            new OpenCvSharp.Size(boundW, boundH),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.White);

        return rotated;
    }

    private static List<WordCandidate> RunOcr(TesseractEngine engine, Mat image)
    {
        using var pix = MatToPix(image);
        using var page = engine.Process(pix);
        using var iterator = page.GetIterator();

        var words = new List<WordCandidate>();
        iterator.Begin();

        do
        {
            var text = iterator.GetText(PageIteratorLevel.Word);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var wordRect))
            {
                continue;
            }

            var hasLineRect = iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var lineRect);
            var hasBlockRect = iterator.TryGetBoundingBox(PageIteratorLevel.Block, out var blockRect);

            var rawConfidence = iterator.GetConfidence(PageIteratorLevel.Word);
            var normalizedConfidence = Math.Clamp(rawConfidence / 100.0, 0, 1);

            words.Add(new WordCandidate
            {
                Text = text.Trim(),
                Confidence = normalizedConfidence,
                ConfidenceRaw = rawConfidence,
                WordBbox = ToBbox(wordRect),
                LineBbox = hasLineRect ? ToBbox(lineRect) : null,
                BlockBbox = hasBlockRect ? ToBbox(blockRect) : null
            });
        }
        while (iterator.Next(PageIteratorLevel.Word));

        return words;
    }

    private static Pix MatToPix(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        return Pix.LoadFromMemory(bytes);
    }

    private static LayoutResult BuildLayout(
        List<WordCandidate> words,
        int pageIndex,
        ConfidenceDefinitionInfo confidenceDefinition,
        int pageWidth,
        int pageHeight,
        ILineReconstructor lineReconstructor,
        ITokenCleanupService tokenCleanupService,
        List<LineReconstructionDiagnosticsInfo> lineDiagnostics)
    {
        if (words.Count == 0)
        {
            lineDiagnostics.Add(new LineReconstructionDiagnosticsInfo
            {
                PageIndex = pageIndex,
                OriginalLineCount = 0,
                ReconstructedLineCount = 0,
                TokensAssigned = 0,
                Successful = true
            });
            return new LayoutResult([], [], [], string.Empty, new TokenCleanupResult());
        }

        var orderedWords = words
            .OrderBy(w => w.LineBbox?.Y ?? w.WordBbox.Y)
            .ThenBy(w => w.LineBbox?.X ?? w.WordBbox.X)
            .ThenBy(w => w.WordBbox.X)
            .ToList();

        var lineGroups = CreateLineGroups(orderedWords);
        var lineEntries = new List<LineBuildEntry>(lineGroups.Count);
        var tokenBlockKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        var tokenIndex = 1;
        var lineIndex = 1;

        foreach (var group in lineGroups.OrderBy(g => g.SortY).ThenBy(g => g.SortX))
        {
            var lineId = $"ln-{pageIndex:000}-{lineIndex:00000}";
            lineIndex++;

            var orderedGroupWords = group.Words.OrderBy(w => w.WordBbox.X).ThenBy(w => w.WordBbox.Y).ToList();
            var lineTokenIds = new List<string>(orderedGroupWords.Count);
            var lineTokenConfs = new List<double>(orderedGroupWords.Count);
            var tokenEntries = new List<TokenInfo>(orderedGroupWords.Count);

            foreach (var word in orderedGroupWords)
            {
                var tokenId = $"t-{pageIndex:000}-{tokenIndex:000000}";
                tokenIndex++;

                lineTokenIds.Add(tokenId);
                lineTokenConfs.Add(word.Confidence);

                tokenEntries.Add(new TokenInfo
                {
                    Id = tokenId,
                    Type = "word",
                    Text = word.Text,
                    Confidence = word.Confidence,
                    ConfidenceRaw = word.ConfidenceRaw,
                    IsLowConfidence = word.Confidence < confidenceDefinition.LowTokenThreshold,
                    Bbox = ClampBbox(word.WordBbox, pageWidth, pageHeight),
                    BlockId = string.Empty,
                    LineId = lineId,
                    Alternates =
                    [
                        new TokenAlternateInfo
                        {
                            Text = word.Text,
                            Confidence = word.Confidence
                        }
                    ],
                    Source = new TokenSourceInfo
                    {
                        Engine = "tesseract",
                        Level = "word"
                    }
                });
                tokenBlockKeys[tokenId] = group.BlockKey;
            }

            var lineBBox = ClampBbox(UnionBboxes(tokenEntries.Select(t => t.Bbox)), pageWidth, pageHeight);
            var lineConfidence = lineTokenConfs.Count == 0 ? 0 : lineTokenConfs.Average();

            var line = new LineInfo
            {
                LineId = lineId,
                Bbox = lineBBox,
                TokenIds = lineTokenIds,
                Confidence = lineConfidence,
                IsLowConfidence = lineConfidence < confidenceDefinition.LowLineThreshold
            };

            lineEntries.Add(new LineBuildEntry(line, tokenEntries, group.BlockKey));
        }

        var tokens = lineEntries.SelectMany(l => l.Tokens).ToList();
        var originalLines = lineEntries.Select(l => l.Line).ToList();

        var cleanup = tokenCleanupService.Cleanup(tokens, []);
        foreach (var token in tokens)
        {
            if (cleanup.ReconstructedTextOverrides.TryGetValue(token.Id, out var updatedText) &&
                !string.IsNullOrWhiteSpace(updatedText))
            {
                token.Text = updatedText;
            }
        }
        var reconstruction = lineReconstructor.Reconstruct(
            tokens,
            pageIndex,
            pageWidth,
            pageHeight,
            confidenceDefinition.LowLineThreshold,
            cleanup.SkipTokenIds,
            cleanup.ReconstructedTextOverrides);

        var lines = reconstruction.Successful && reconstruction.Lines.Count > 0
            ? reconstruction.Lines
            : originalLines;

        var fullText = reconstruction.Successful && !string.IsNullOrWhiteSpace(reconstruction.FullText)
            ? reconstruction.FullText
            : string.Join(
                Environment.NewLine,
                lines.Select(line => string.Join(' ', line.TokenIds.Select(id => tokens.First(t => t.Id == id).Text).Where(t => !string.IsNullOrWhiteSpace(t)))));

        lineDiagnostics.Add(new LineReconstructionDiagnosticsInfo
        {
            PageIndex = pageIndex,
            OriginalLineCount = originalLines.Count,
            ReconstructedLineCount = lines.Count,
            TokensAssigned = reconstruction.TokensAssigned,
            Successful = reconstruction.Successful
        });

        var blockKeyByLineId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var dominantBlockKey = line.TokenIds
                .Select(id => tokenBlockKeys.GetValueOrDefault(id, "blk-fallback"))
                .GroupBy(k => k)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)
                .FirstOrDefault() ?? "blk-fallback";
            blockKeyByLineId[line.LineId] = dominantBlockKey;
        }

        var blockGroups = lines
            .GroupBy(l => blockKeyByLineId.GetValueOrDefault(l.LineId, "blk-fallback"))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var blocks = new List<BlockInfo>(blockGroups.Count);

        var blockIndex = 1;
        var tokenById = tokens.ToDictionary(t => t.Id, t => t);

        foreach (var group in blockGroups)
        {
            var blockId = $"blk-{pageIndex:000}-{blockIndex:00000}";
            blockIndex++;

            var groupLines = group.OrderBy(l => l.Bbox.Y).ThenBy(l => l.Bbox.X).ToList();
            var lineIds = groupLines.Select(l => l.LineId).ToList();
            var tokenIds = groupLines.SelectMany(x => x.TokenIds).Distinct(StringComparer.Ordinal).ToList();
            var bbox = ClampBbox(UnionBboxes(groupLines.Select(l => l.Bbox)), pageWidth, pageHeight);
            var confidence = groupLines.Count == 0 ? 0 : groupLines.Average(l => l.Confidence);

            foreach (var tokenId in tokenIds)
            {
                tokenById[tokenId].BlockId = blockId;
            }

            blocks.Add(new BlockInfo
            {
                BlockId = blockId,
                Type = "text",
                Bbox = bbox,
                LineIds = lineIds,
                TokenIds = tokenIds,
                Confidence = confidence,
                IsLowConfidence = confidence < confidenceDefinition.LowBlockThreshold
            });
        }

        var finalTokens = lines
            .SelectMany(line => line.TokenIds)
            .Select(id => tokenById[id])
            .ToList();

        return new LayoutResult(finalTokens, lines, blocks, fullText, cleanup);
    }

    private static List<LineGroup> CreateLineGroups(List<WordCandidate> orderedWords)
    {
        var groups = new List<LineGroup>();

        var hasLineBoxes = orderedWords.All(w => w.LineBbox is not null);
        if (hasLineBoxes)
        {
            foreach (var group in orderedWords
                         .GroupBy(w => BboxKey(w.LineBbox!))
                         .OrderBy(g => g.Min(x => x.LineBbox!.Y))
                         .ThenBy(g => g.Min(x => x.LineBbox!.X)))
            {
                var first = group.First();
                groups.Add(new LineGroup
                {
                    SortY = first.LineBbox!.Y,
                    SortX = first.LineBbox.X,
                    BlockKey = first.BlockBbox is null ? "blk-fallback" : BboxKey(first.BlockBbox),
                    Words = group.ToList()
                });
            }

            return groups;
        }

        var sorted = orderedWords.OrderBy(w => w.WordBbox.Y).ThenBy(w => w.WordBbox.X).ToList();
        foreach (var word in sorted)
        {
            var tolerance = Math.Max(8, word.WordBbox.H / 2);
            var group = groups.FirstOrDefault(g => Math.Abs(g.SortY - word.WordBbox.Y) <= tolerance);

            if (group is null)
            {
                groups.Add(new LineGroup
                {
                    SortY = word.WordBbox.Y,
                    SortX = word.WordBbox.X,
                    BlockKey = word.BlockBbox is null ? "blk-fallback" : BboxKey(word.BlockBbox),
                    Words = [word]
                });
            }
            else
            {
                group.Words.Add(word);
                group.SortY = Math.Min(group.SortY, word.WordBbox.Y);
                group.SortX = Math.Min(group.SortX, word.WordBbox.X);
            }
        }

        return groups.OrderBy(g => g.SortY).ThenBy(g => g.SortX).ToList();
    }

    private static string BboxKey(BboxInfo bbox)
    {
        return $"{bbox.X},{bbox.Y},{bbox.W},{bbox.H}";
    }

    private static BboxInfo ToBbox(Tesseract.Rect rect)
    {
        return new BboxInfo
        {
            X = rect.X1,
            Y = rect.Y1,
            W = Math.Max(0, rect.X2 - rect.X1),
            H = Math.Max(0, rect.Y2 - rect.Y1)
        };
    }

    private static BboxInfo UnionBboxes(IEnumerable<BboxInfo> boxes)
    {
        var list = boxes.ToList();
        if (list.Count == 0)
        {
            return new BboxInfo();
        }

        var minX = list.Min(b => b.X);
        var minY = list.Min(b => b.Y);
        var maxX = list.Max(b => b.X + b.W);
        var maxY = list.Max(b => b.Y + b.H);

        return new BboxInfo
        {
            X = minX,
            Y = minY,
            W = Math.Max(0, maxX - minX),
            H = Math.Max(0, maxY - minY)
        };
    }

    private static BboxInfo ClampBbox(BboxInfo bbox, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return bbox;
        }

        var x = Math.Clamp(bbox.X, 0, width - 1);
        var y = Math.Clamp(bbox.Y, 0, height - 1);
        var maxX = Math.Clamp(bbox.X + bbox.W, x, width);
        var maxY = Math.Clamp(bbox.Y + bbox.H, y, height);

        return new BboxInfo
        {
            X = x,
            Y = y,
            W = Math.Max(0, maxX - x),
            H = Math.Max(0, maxY - y)
        };
    }

    private static OcrContractRoot BuildContract(string filePath, OcrOptions options, string fileType, string mimeType)
    {
        var fileName = string.IsNullOrWhiteSpace(filePath) ? "input.pdf" : Path.GetFileName(filePath);
        var tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

        return new OcrContractRoot
        {
            Document = new DocumentInfo
            {
                CreatedUtc = DateTime.UtcNow.ToString("O"),
                Source = new SourceInfo
                {
                    OriginalFileName = fileName,
                    FileType = fileType,
                    PageCount = 1,
                    MimeType = mimeType,
                    FileHashSha256 = null
                },
                Processing = new ProcessingInfo
                {
                    Engine = new EngineInfo
                    {
                        Language = [string.IsNullOrWhiteSpace(options.Language) ? "eng" : options.Language]
                    },
                    Render = new RenderInfo
                    {
                        DpiOriginal = options.TargetDpi,
                        DpiNormalizedTo = options.TargetDpi
                    },
                    Preprocessing = new PreprocessingInfo
                    {
                        Rotation = new RotationInfo
                        {
                            Attempted = false,
                            Applied = false,
                            Degrees = 0,
                            Confidence = 0
                        },
                        Deskew = new DeskewInfo
                        {
                            Attempted = options.EnableDeskew,
                            Applied = false,
                            Degrees = 0,
                            Confidence = 0
                        },
                        Denoise = new MethodToggleInfo
                        {
                            Enabled = options.EnableDenoise,
                            Method = options.DenoiseMethod
                        },
                        Binarization = new MethodToggleInfo
                        {
                            Enabled = options.EnableBinarization,
                            Method = options.BinarizationMethod
                        },
                        ContrastEnhancement = new MethodToggleInfo
                        {
                            Enabled = options.EnableContrastEnhancement,
                            Method = options.EnableContrastEnhancement ? options.ContrastMethod : null
                        }
                    }
                }
            },
            Extensions = new ExtensionsInfo
            {
                TessdataPath = tessdataPath,
                OptionSnapshot = new OptionSnapshotInfo
                {
                    TargetDpi = options.TargetDpi,
                    Language = options.Language,
                    PageSegMode = options.PageSegMode,
                    EngineMode = options.EngineMode,
                    PreserveInterwordSpaces = options.PreserveInterwordSpaces,
                    SaveTokenOverlay = options.SaveTokenOverlay,
                    EnableNoiseFiltering = options.EnableNoiseFiltering,
                    EnableDeskew = options.EnableDeskew,
                    MaxDeskewDegrees = options.MaxDeskewDegrees,
                    DeskewAngleStep = options.DeskewAngleStep,
                    MinDeskewConfidence = options.MinDeskewConfidence,
                    EnableDenoise = options.EnableDenoise,
                    DenoiseMethod = options.DenoiseMethod,
                    DenoiseKernel = options.DenoiseKernel,
                    EnableBinarization = options.EnableBinarization,
                    BinarizationMethod = options.BinarizationMethod,
                    EnableContrastEnhancement = options.EnableContrastEnhancement,
                    ContrastMethod = options.ContrastMethod,
                    SaveJsonToDisk = options.SaveJsonToDisk,
                    OutputFolder = options.OutputFolder,
                    SaveDebugArtifacts = options.SaveDebugArtifacts,
                    ProfileName = options.ProfileName
                },
                PagePreprocessing = []
            }
        };
    }

    private static string? PrepareRunOutputFolder(string? filePath, OcrOptions options)
    {
        if (!options.SaveJsonToDisk && !options.SaveDebugArtifacts && !options.SaveTokenOverlay)
        {
            return null;
        }

        var baseFolder = string.IsNullOrWhiteSpace(options.OutputFolder)
            ? Path.Combine(AppContext.BaseDirectory, "output")
            : options.OutputFolder;

        Directory.CreateDirectory(baseFolder);

        var fileNameNoExt = string.IsNullOrWhiteSpace(filePath)
            ? "input"
            : Path.GetFileNameWithoutExtension(filePath);

        if (string.IsNullOrWhiteSpace(fileNameNoExt))
        {
            fileNameNoExt = "input";
        }

        var subFolder = $"{fileNameNoExt}_{DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}";
        var runOutputFolder = Path.Combine(baseFolder, subFolder);
        Directory.CreateDirectory(runOutputFolder);
        return runOutputFolder;
    }

    private static string SaveJson(string json, string? filePath, string? configuredOutputFolder, string? runOutputFolder)
    {
        var outputFolder = runOutputFolder;
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            var tempOptions = new OcrOptions
            {
                OutputFolder = configuredOutputFolder,
                SaveJsonToDisk = true,
                SaveDebugArtifacts = false
            };
            outputFolder = PrepareRunOutputFolder(filePath ?? string.Empty, tempOptions);
        }

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            throw new InvalidOperationException("Failed to create output folder.");
        }

        var outputPath = Path.Combine(outputFolder, "result.json");
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    private static string DetermineFileType(string extension)
    {
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "pdf";
        }

        if (SupportedExtensions.Contains(extension))
        {
            return "image";
        }

        return "image";
    }

    private static string DetermineMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".tif" or ".tiff" => "image/tiff",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }

    private static void AddError(OcrContractRoot root, string code, string message, int? pageIndex = null)
    {
        root.Errors.Add(new IssueInfo
        {
            Code = code,
            Severity = "error",
            Message = message,
            PageIndex = pageIndex
        });
    }

    private static void AddWarning(OcrContractRoot root, string code, string message, int? pageIndex = null, Dictionary<string, object?>? details = null)
    {
        root.Warnings.Add(new IssueInfo
        {
            Code = code,
            Severity = "warning",
            Message = message,
            PageIndex = pageIndex,
            Details = details ?? new Dictionary<string, object?>()
        });
    }

    private static void AddInfo(OcrContractRoot root, string code, string message, int? pageIndex = null, Dictionary<string, object?>? details = null)
    {
        root.Warnings.Add(new IssueInfo
        {
            Code = code,
            Severity = "info",
            Message = message,
            PageIndex = pageIndex,
            Details = details ?? new Dictionary<string, object?>()
        });
    }

    private static Dictionary<string, object?> CreateExceptionDetails(Exception ex)
    {
        var details = new Dictionary<string, object?>
        {
            ["exceptionType"] = ex.GetType().FullName,
            ["message"] = ex.Message
        };

        if (!string.IsNullOrWhiteSpace(ex.InnerException?.Message))
        {
            details["innerExceptionType"] = ex.InnerException.GetType().FullName;
            details["innerMessage"] = ex.InnerException.Message;
        }

        return details;
    }

    private static EngineMode ResolveEngineMode(int? configuredEngineMode, OcrContractRoot root)
    {
        if (!configuredEngineMode.HasValue)
        {
            return EngineMode.Default;
        }

        if (Enum.IsDefined(typeof(EngineMode), configuredEngineMode.Value))
        {
            return (EngineMode)configuredEngineMode.Value;
        }

        AddWarning(root, "invalid_engine_mode", $"Unsupported EngineMode value '{configuredEngineMode.Value}'. Falling back to Default.");
        return EngineMode.Default;
    }

    private static PageSegMode ResolvePageSegMode(int? configuredPageSegMode, OcrContractRoot root)
    {
        if (!configuredPageSegMode.HasValue)
        {
            return PageSegMode.Auto;
        }

        if (Enum.IsDefined(typeof(PageSegMode), configuredPageSegMode.Value))
        {
            return (PageSegMode)configuredPageSegMode.Value;
        }

        AddWarning(root, "invalid_page_seg_mode", $"Unsupported PageSegMode value '{configuredPageSegMode.Value}'. Falling back to Auto.");
        return PageSegMode.Auto;
    }

    private static void AddQualityWarnings(OcrContractRoot root, PageInfo page, PreprocessResult preprocessResult)
    {
        if (page.Quality.MeanTokenConfidence > 0 && page.Quality.MeanTokenConfidence < MeanConfidenceWarningThreshold)
        {
            AddWarning(
                root,
                "low_page_confidence",
                $"Page {page.PageIndex} mean token confidence is low ({page.Quality.MeanTokenConfidence:F3}).",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["meanTokenConfidence"] = page.Quality.MeanTokenConfidence,
                    ["threshold"] = MeanConfidenceWarningThreshold
                });
        }

        if (page.Quality.LowConfidenceTokenCount >= LowConfidenceCountWarningThreshold)
        {
            AddWarning(
                root,
                "high_low_confidence_token_count",
                $"Page {page.PageIndex} has many low-confidence tokens ({page.Quality.LowConfidenceTokenCount}).",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["lowConfidenceTokenCount"] = page.Quality.LowConfidenceTokenCount,
                    ["threshold"] = LowConfidenceCountWarningThreshold
                });
        }

        if (preprocessResult.DeskewAttempted &&
            !preprocessResult.DeskewApplied &&
            Math.Abs(preprocessResult.CandidateDegrees) >= LargeDeskewAngleWarningThreshold &&
            preprocessResult.CandidateConfidence > 0)
        {
            AddWarning(
                root,
                "deskew_rejected_low_confidence",
                $"Page {page.PageIndex} detected candidate deskew angle {preprocessResult.CandidateDegrees:F2} but confidence {preprocessResult.CandidateConfidence:F3} was too low to apply.",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["detectedDegrees"] = preprocessResult.CandidateDegrees,
                    ["detectedConfidence"] = preprocessResult.CandidateConfidence
                });
        }

        if (page.Quality.BlankPage && page.Tokens.Count == 0)
        {
            AddWarning(
                root,
                "unexpected_blank_page",
                $"Page {page.PageIndex} appears blank after OCR.",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["meanTokenConfidence"] = page.Quality.MeanTokenConfidence
                });
        }
    }

    private static NoiseAnalysis AnalyzePageNoise(LayoutResult layout)
    {
        var totalTokenCount = layout.Tokens.Count;
        if (totalTokenCount == 0)
        {
            return new NoiseAnalysis(0, 0, 0, 0, 0, 0);
        }

        var tinyTokenCount = layout.Tokens.Count(t => (t.Bbox.W * t.Bbox.H) <= VerySmallTokenAreaThreshold);
        var lowConfidenceTokenCount = layout.Tokens.Count(t => t.IsLowConfidence);
        var symbolLikeTokenCount = layout.Tokens.Count(t => IsSymbolLike(t.Text));

        var lineTokenCounts = layout.Lines.ToDictionary(l => l.LineId, l => l.TokenIds.Count);
        var suspectedNoiseCount = layout.Tokens.Count(t =>
            (t.Bbox.W * t.Bbox.H) <= VerySmallTokenAreaThreshold &&
            t.Confidence < 0.45 &&
            IsSymbolLike(t.Text) &&
            lineTokenCounts.GetValueOrDefault(t.LineId, 0) <= 2);

        var ratio = (double)suspectedNoiseCount / totalTokenCount;
        return new NoiseAnalysis(totalTokenCount, lowConfidenceTokenCount, tinyTokenCount, symbolLikeTokenCount, suspectedNoiseCount, ratio);
    }

    private static List<string> ApplyConservativeNoiseFiltering(LayoutResult layout, NoiseAnalysis noiseAnalysis)
    {
        if (layout.Tokens.Count == 0 || noiseAnalysis.SuspectedDecorativeNoiseCount == 0)
        {
            return [];
        }

        var lineTokenCounts = layout.Lines.ToDictionary(l => l.LineId, l => l.TokenIds.Count);
        var toRemove = layout.Tokens
            .Where(t =>
                (t.Bbox.W * t.Bbox.H) <= VerySmallTokenAreaThreshold &&
                t.Confidence < 0.35 &&
                IsSymbolLike(t.Text) &&
                lineTokenCounts.GetValueOrDefault(t.LineId, 0) <= 2)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (toRemove.Count == 0)
        {
            return [];
        }

        layout.Tokens.RemoveAll(t => toRemove.Contains(t.Id));

        foreach (var line in layout.Lines)
        {
            line.TokenIds = line.TokenIds.Where(id => !toRemove.Contains(id)).ToList();
            if (line.TokenIds.Count == 0)
            {
                continue;
            }

            var lineTokens = layout.Tokens.Where(t => line.TokenIds.Contains(t.Id)).ToList();
            line.Bbox = UnionBboxes(lineTokens.Select(t => t.Bbox));
            line.Confidence = lineTokens.Average(t => t.Confidence);
            line.IsLowConfidence = line.Confidence < 0.75;
        }

        layout.Lines.RemoveAll(l => l.TokenIds.Count == 0);

        foreach (var block in layout.Blocks)
        {
            block.TokenIds = block.TokenIds.Where(id => !toRemove.Contains(id)).ToList();
            block.LineIds = block.LineIds.Where(id => layout.Lines.Any(l => l.LineId == id)).ToList();
            if (block.LineIds.Count == 0 || block.TokenIds.Count == 0)
            {
                continue;
            }

            var blockLines = layout.Lines.Where(l => block.LineIds.Contains(l.LineId)).ToList();
            block.Bbox = UnionBboxes(blockLines.Select(l => l.Bbox));
            block.Confidence = blockLines.Average(l => l.Confidence);
            block.IsLowConfidence = block.Confidence < 0.8;
        }

        layout.Blocks.RemoveAll(b => b.LineIds.Count == 0 || b.TokenIds.Count == 0);

        var lineTexts = layout.Lines
            .OrderBy(l => l.Bbox.Y)
            .ThenBy(l => l.Bbox.X)
            .Select(line =>
            {
                var tokenById = layout.Tokens.ToDictionary(t => t.Id, t => t);
                return string.Join(' ', line.TokenIds.Where(tokenById.ContainsKey).Select(id => tokenById[id].Text));
            })
            .Where(t => !string.IsNullOrWhiteSpace(t));
        layout.FullText = string.Join(Environment.NewLine, lineTexts);

        return [.. toRemove.OrderBy(id => id)];
    }

    private static void AddNoiseWarnings(
        OcrContractRoot root,
        PageInfo page,
        NoiseAnalysis noise,
        bool filteringEnabled,
        int filteredThisPage)
    {
        if (noise.SuspectedDecorativeNoiseRatio >= NoiseWarningRatioThreshold &&
            noise.SuspectedDecorativeNoiseCount > 0 &&
            noise.TinyTokenCount >= VerySmallTokenCountThreshold)
        {
            AddWarning(
                root,
                "excessive_tiny_tokens",
                $"Page {page.PageIndex} has high decorative noise ratio ({noise.SuspectedDecorativeNoiseRatio:P1}).",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["suspectedDecorativeNoiseCount"] = noise.SuspectedDecorativeNoiseCount,
                    ["suspectedDecorativeNoiseRatio"] = noise.SuspectedDecorativeNoiseRatio,
                    ["ratioThreshold"] = NoiseWarningRatioThreshold,
                    ["tinyTokenCount"] = noise.TinyTokenCount,
                    ["tinyTokenCountThreshold"] = VerySmallTokenCountThreshold
                });
        }

        if (filteringEnabled && filteredThisPage >= FilteredTokenWarningThreshold)
        {
            AddWarning(
                root,
                "noise_filtering_removed_many_tokens",
                $"Page {page.PageIndex} filtered {filteredThisPage} suspected decorative tokens.",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["filteredTokenCount"] = filteredThisPage,
                    ["threshold"] = FilteredTokenWarningThreshold
                });
        }
    }

    private static void AddRegionWarnings(OcrContractRoot root, PageInfo page, RegionDetectionDiagnostics diagnostics)
    {
        AddInfo(
            root,
            "region_detection_stats",
            $"Page {page.PageIndex} region detection: raw={diagnostics.RawCandidateCount}, geometry={diagnostics.GeometryFilteredCount}, label={diagnostics.LabelFilteredCount}, finalCheckbox={diagnostics.FinalCheckboxCount}, finalRadio={diagnostics.FinalRadioCount}.",
            page.PageIndex,
            new Dictionary<string, object?>
            {
                ["rawCandidateCount"] = diagnostics.RawCandidateCount,
                ["geometryFilteredCount"] = diagnostics.GeometryFilteredCount,
                ["labelFilteredCount"] = diagnostics.LabelFilteredCount,
                ["finalCheckboxCount"] = diagnostics.FinalCheckboxCount,
                ["finalRadioCount"] = diagnostics.FinalRadioCount
            });

        if (page.Regions.Count == 0)
        {
            return;
        }

        var suspiciousThreshold = Math.Max(25, page.Tokens.Count / 3);
        if (page.Regions.Count > suspiciousThreshold)
        {
            AddWarning(
                root,
                "high_region_false_positive_risk",
                $"Page {page.PageIndex} produced unusually high region count ({page.Regions.Count}).",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["regionCount"] = page.Regions.Count,
                    ["threshold"] = suspiciousThreshold
                });
        }

        var unlabeledCount = page.Regions.Count(r => r.LabelTokenIds.Count == 0);
        if (unlabeledCount >= 5)
        {
            AddWarning(
                root,
                "ambiguous_label_association",
                $"Page {page.PageIndex} has {unlabeledCount} regions without nearby label tokens.",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["unlabeledRegionCount"] = unlabeledCount,
                    ["totalRegions"] = page.Regions.Count
                });
        }

        var conflictingCount = page.Regions
            .Where(r => r.LabelTokenIds.Count > 0)
            .GroupBy(r => string.Join("|", r.LabelTokenIds))
            .Count(g => g.Select(r => r.Value).Distinct().Count() > 1);

        if (conflictingCount > 0)
        {
            AddWarning(
                root,
                "conflicting_checked_state_detection",
                $"Page {page.PageIndex} has {conflictingCount} conflicting region checked-state groups for the same label tokens.",
                page.PageIndex,
                new Dictionary<string, object?>
                {
                    ["conflictingGroups"] = conflictingCount
                });
        }
    }

    private static void AddTokenCleanupInfo(OcrContractRoot root, int pageIndex, TokenCleanupResult cleanup)
    {
        AddInfo(
            root,
            "token_cleanup_stats",
            $"Page {pageIndex} token cleanup: original={cleanup.TokensOriginal}, modified={cleanup.TokensModified}, removed={cleanup.TokensRemoved}, split={cleanup.TokensSplit}, checkboxArtifactsRemoved={cleanup.CheckboxArtifactsRemoved}, underlineArtifactsRemoved={cleanup.UnderlineArtifactsRemoved}, dictionaryCorrections={cleanup.DictionaryCorrections}.",
            pageIndex,
            new Dictionary<string, object?>
            {
                ["tokensOriginal"] = cleanup.TokensOriginal,
                ["tokensModified"] = cleanup.TokensModified,
                ["tokensRemoved"] = cleanup.TokensRemoved,
                ["tokensSplit"] = cleanup.TokensSplit,
                ["checkboxArtifactsRemoved"] = cleanup.CheckboxArtifactsRemoved,
                ["underlineArtifactsRemoved"] = cleanup.UnderlineArtifactsRemoved,
                ["dictionaryCorrections"] = cleanup.DictionaryCorrections
            });
    }

    private static void RenumberKeyValueCandidates(PageInfo page)
    {
        var ordered = page.KeyValueCandidates
            .OrderBy(c => c.Label.Bbox.Y)
            .ThenBy(c => c.Label.Bbox.X)
            .ThenBy(c => c.Value.Bbox.Y)
            .ThenBy(c => c.Value.Bbox.X)
            .ThenByDescending(c => c.Confidence)
            .ToList();

        page.KeyValueCandidates.Clear();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].PairId = $"kv-{page.PageIndex:000}-{i + 1:00000}";
            page.KeyValueCandidates.Add(ordered[i]);
        }
    }

    private static List<RecognitionFieldInfo> MergeRecognitionFields(
        IReadOnlyList<RecognitionFieldInfo> baseFields,
        IReadOnlyList<RecognitionFieldInfo> additionalFields,
        double lowFieldThreshold)
    {
        var merged = new Dictionary<string, RecognitionFieldInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in baseFields)
        {
            if (!string.IsNullOrWhiteSpace(field.FieldId))
            {
                merged[field.FieldId] = field;
            }
        }

        foreach (var field in additionalFields)
        {
            if (string.IsNullOrWhiteSpace(field.FieldId))
            {
                continue;
            }

            if (merged.TryGetValue(field.FieldId, out var existing))
            {
                if (field.Confidence > existing.Confidence)
                {
                    field.IsLowConfidence = field.Confidence < lowFieldThreshold;
                    merged[field.FieldId] = field;
                }
            }
            else
            {
                field.IsLowConfidence = field.Confidence < lowFieldThreshold;
                merged[field.FieldId] = field;
            }
        }

        return merged.Values
            .OrderBy(f => f.Source.PageIndex)
            .ThenBy(f => f.FieldId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSymbolLike(string text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length > 0 && SymbolLikeRegex.IsMatch(trimmed);
    }

    private static List<string> BuildUniqueWordList(IEnumerable<TokenInfo> tokens)
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token.Text))
            {
                continue;
            }

            var normalizedKey = NormalizeWordKey(token.Text);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            if (!words.ContainsKey(normalizedKey))
            {
                words[normalizedKey] = token.Text.Trim();
            }
        }

        return words.Values
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void MergeWordsIntoDocumentMap(IEnumerable<string> pageWords, Dictionary<string, string> documentWordMap)
    {
        foreach (var word in pageWords)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                continue;
            }

            var normalizedKey = NormalizeWordKey(word);
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            if (!documentWordMap.ContainsKey(normalizedKey))
            {
                documentWordMap[normalizedKey] = word.Trim();
            }
        }
    }

    private static string NormalizeWordKey(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var collapsed = string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToLowerInvariant();
    }

    private static OverlayArtifactPaths SaveOverlayArtifacts(Mat source, LayoutResult layout, int pageIndex, string outputFolder)
    {
        using var tokenOverlay = new Mat();
        using var lineOverlay = new Mat();
        using var blockOverlay = new Mat();
        if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, tokenOverlay, ColorConversionCodes.GRAY2BGR);
            Cv2.CvtColor(source, lineOverlay, ColorConversionCodes.GRAY2BGR);
            Cv2.CvtColor(source, blockOverlay, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            source.CopyTo(tokenOverlay);
            source.CopyTo(lineOverlay);
            source.CopyTo(blockOverlay);
        }

        foreach (var token in layout.Tokens)
        {
            var rect = new OpenCvSharp.Rect(token.Bbox.X, token.Bbox.Y, token.Bbox.W, token.Bbox.H);
            Cv2.Rectangle(tokenOverlay, rect, new Scalar(0, 255, 0), 1);
            var label = string.IsNullOrWhiteSpace(token.Text) ? token.Id : token.Text;
            var labelY = Math.Max(12, token.Bbox.Y - 3);
            Cv2.PutText(tokenOverlay, label, new OpenCvSharp.Point(token.Bbox.X, labelY), HersheyFonts.HersheySimplex, 0.35, new Scalar(0, 0, 255), 1);
        }

        foreach (var line in layout.Lines)
        {
            var rect = new OpenCvSharp.Rect(line.Bbox.X, line.Bbox.Y, line.Bbox.W, line.Bbox.H);
            Cv2.Rectangle(lineOverlay, rect, new Scalar(255, 200, 0), 2);
            var labelY = Math.Max(12, line.Bbox.Y - 4);
            Cv2.PutText(lineOverlay, line.LineId, new OpenCvSharp.Point(line.Bbox.X, labelY), HersheyFonts.HersheySimplex, 0.4, new Scalar(0, 128, 255), 1);
        }

        foreach (var block in layout.Blocks)
        {
            var rect = new OpenCvSharp.Rect(block.Bbox.X, block.Bbox.Y, block.Bbox.W, block.Bbox.H);
            Cv2.Rectangle(blockOverlay, rect, new Scalar(255, 0, 255), 2);
            var labelY = Math.Max(12, block.Bbox.Y - 4);
            Cv2.PutText(blockOverlay, block.BlockId, new OpenCvSharp.Point(block.Bbox.X, labelY), HersheyFonts.HersheySimplex, 0.45, new Scalar(255, 64, 64), 1);
        }

        var tokenOverlayPath = Path.Combine(outputFolder, $"page_{pageIndex}_tokens_overlay.png");
        var lineOverlayPath = Path.Combine(outputFolder, $"page_{pageIndex}_lines_overlay.png");
        var blockOverlayPath = Path.Combine(outputFolder, $"page_{pageIndex}_blocks_overlay.png");
        Cv2.ImWrite(tokenOverlayPath, tokenOverlay);
        Cv2.ImWrite(lineOverlayPath, lineOverlay);
        Cv2.ImWrite(blockOverlayPath, blockOverlay);

        return new OverlayArtifactPaths(tokenOverlayPath, lineOverlayPath, blockOverlayPath);
    }

    private static string SaveTableOverlay(Mat source, IReadOnlyCollection<TableOverlayInfo> overlays, int pageIndex, string outputFolder)
    {
        using var overlay = new Mat();
        if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            source.CopyTo(overlay);
        }

        foreach (var table in overlays)
        {
            var tableRect = new OpenCvSharp.Rect(table.TableBbox.X, table.TableBbox.Y, table.TableBbox.W, table.TableBbox.H);
            Cv2.Rectangle(overlay, tableRect, new Scalar(0, 255, 255), 2);
            var methodLabelY = Math.Max(12, table.TableBbox.Y - 4);
            Cv2.PutText(
                overlay,
                table.Method,
                new OpenCvSharp.Point(table.TableBbox.X, methodLabelY),
                HersheyFonts.HersheySimplex,
                0.5,
                new Scalar(0, 255, 255),
                1);

            foreach (var y in table.HorizontalLinesY)
            {
                Cv2.Line(
                    overlay,
                    new OpenCvSharp.Point(table.TableBbox.X, y),
                    new OpenCvSharp.Point(table.TableBbox.X + table.TableBbox.W, y),
                    new Scalar(255, 0, 0),
                    1);
            }

            foreach (var x in table.VerticalLinesX)
            {
                Cv2.Line(
                    overlay,
                    new OpenCvSharp.Point(x, table.TableBbox.Y),
                    new OpenCvSharp.Point(x, table.TableBbox.Y + table.TableBbox.H),
                    new Scalar(255, 255, 0),
                    1);
            }

            foreach (var row in table.RowBands)
            {
                var rr = new OpenCvSharp.Rect(row.X, row.Y, row.W, row.H);
                Cv2.Rectangle(overlay, rr, new Scalar(255, 0, 0), 1);
            }

            foreach (var col in table.ColBands)
            {
                var cr = new OpenCvSharp.Rect(col.X, col.Y, col.W, col.H);
                Cv2.Rectangle(overlay, cr, new Scalar(255, 255, 0), 1);
            }

            foreach (var cell in table.Cells)
            {
                var cellRect = new OpenCvSharp.Rect(cell.X, cell.Y, cell.W, cell.H);
                Cv2.Rectangle(overlay, cellRect, new Scalar(144, 238, 144), 1);
            }
        }

        var path = Path.Combine(outputFolder, $"page_{pageIndex}_table_overlay.png");
        Cv2.ImWrite(path, overlay);
        return path;
    }

    private static string SaveRegionOverlay(Mat source, IReadOnlyCollection<RegionOverlayInfo> regions, int pageIndex, string outputFolder)
    {
        using var overlay = new Mat();
        if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            source.CopyTo(overlay);
        }

        foreach (var region in regions)
        {
            var rect = new OpenCvSharp.Rect(region.Bbox.X, region.Bbox.Y, region.Bbox.W, region.Bbox.H);
            var isChecked = region.Value == true;
            var color = string.Equals(region.Type, "radio", StringComparison.OrdinalIgnoreCase)
                ? new Scalar(170, 60, 180)
                : new Scalar(0, 165, 255);
            var thickness = isChecked ? 3 : 1;
            Cv2.Rectangle(overlay, rect, color, thickness);
            if (isChecked)
            {
                var center = new OpenCvSharp.Point(region.Bbox.X + (region.Bbox.W / 2), region.Bbox.Y + (region.Bbox.H / 2));
                if (string.Equals(region.Type, "radio", StringComparison.OrdinalIgnoreCase))
                {
                    Cv2.Circle(overlay, center, Math.Max(2, Math.Min(region.Bbox.W, region.Bbox.H) / 5), color, -1);
                }
                else
                {
                    var p1 = new OpenCvSharp.Point(region.Bbox.X + Math.Max(1, region.Bbox.W / 5), region.Bbox.Y + (region.Bbox.H / 2));
                    var p2 = new OpenCvSharp.Point(region.Bbox.X + (region.Bbox.W / 2), region.Bbox.Y + region.Bbox.H - Math.Max(2, region.Bbox.H / 5));
                    var p3 = new OpenCvSharp.Point(region.Bbox.X + region.Bbox.W - Math.Max(1, region.Bbox.W / 6), region.Bbox.Y + Math.Max(1, region.Bbox.H / 5));
                    Cv2.Line(overlay, p1, p2, color, 2);
                    Cv2.Line(overlay, p2, p3, color, 2);
                }
            }
            var label = $"{region.Type}:{(isChecked ? "checked" : "unchecked")}";
            var labelY = Math.Max(12, region.Bbox.Y - 3);
            Cv2.PutText(overlay, label, new OpenCvSharp.Point(region.Bbox.X, labelY), HersheyFonts.HersheySimplex, 0.4, color, 1);
        }

        var path = Path.Combine(outputFolder, $"page_{pageIndex}_regions_overlay.png");
        Cv2.ImWrite(path, overlay);
        return path;
    }

    private sealed class LoadedPage
    {
        public int PageIndex { get; init; }
        public required Mat Image { get; init; }
        public int RenderMs { get; init; }
        public string? OriginalImagePath { get; init; }
    }

    private sealed class PreprocessResult
    {
        public required Mat ProcessedMat { get; init; }
        public bool DeskewAttempted { get; init; }
        public bool DeskewApplied { get; init; }
        public double CandidateDegrees { get; init; }
        public double CandidateConfidence { get; init; }
        public double AppliedDegrees { get; init; }
        public string? GrayImagePath { get; init; }
        public string? FinalImagePath { get; init; }
    }

    private sealed class WordCandidate
    {
        public required string Text { get; init; }
        public double Confidence { get; init; }
        public double? ConfidenceRaw { get; init; }
        public required BboxInfo WordBbox { get; init; }
        public BboxInfo? LineBbox { get; init; }
        public BboxInfo? BlockBbox { get; init; }
    }

    private sealed class LayoutResult
    {
        public LayoutResult(List<TokenInfo> tokens, List<LineInfo> lines, List<BlockInfo> blocks, string fullText, TokenCleanupResult tokenCleanupStats)
        {
            Tokens = tokens;
            Lines = lines;
            Blocks = blocks;
            FullText = fullText;
            TokenCleanupStats = tokenCleanupStats;
        }

        public List<TokenInfo> Tokens { get; }
        public List<LineInfo> Lines { get; }
        public List<BlockInfo> Blocks { get; }
        public string FullText { get; set; }
        public TokenCleanupResult TokenCleanupStats { get; }
    }

    private sealed class LineGroup
    {
        public int SortY { get; set; }
        public int SortX { get; set; }
        public required string BlockKey { get; init; }
        public required List<WordCandidate> Words { get; init; }
    }

    private sealed class LineBuildEntry
    {
        public LineBuildEntry(LineInfo line, List<TokenInfo> tokens, string blockKey)
        {
            Line = line;
            Tokens = tokens;
            BlockKey = blockKey;
        }

        public LineInfo Line { get; }
        public List<TokenInfo> Tokens { get; }
        public string BlockKey { get; }
    }

    private readonly record struct DeskewEstimation(double Angle, double Confidence);
    private readonly record struct OverlayArtifactPaths(string TokenOverlayPath, string LineOverlayPath, string BlockOverlayPath);
    private readonly record struct NoiseAnalysis(
        int TotalTokenCount,
        int LowConfidenceTokenCount,
        int TinyTokenCount,
        int SymbolLikeTokenCount,
        int SuspectedDecorativeNoiseCount,
        double SuspectedDecorativeNoiseRatio);
}
