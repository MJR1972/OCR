using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Ocr.Core.Contracts;
using Ocr.Core.Services;
using OcrShowcase.Demo.Wpf.Commands;
using OcrShowcase.Demo.Wpf.Models;
using OcrShowcase.Demo.Wpf.Services;

namespace OcrShowcase.Demo.Wpf.ViewModels;

/// <summary>
/// ViewModel for the OCR showcase demo window.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly bool EnableOverlayDebugLogging = false;

    private readonly IInputFileDialogService _inputFileDialogService;
    private readonly IJsonSaveDialogService _jsonSaveDialogService;
    private readonly IOcrDemoService _ocrDemoService;
    private readonly IOverlayProjectionService _overlayProjectionService;
    private readonly IDemoStartupService _demoStartupService;
    private readonly List<PreviewOverlayItem> _allOverlayItems = [];

    private string _statusMessage = "Demo workspace ready. Load a document to begin.";
    private bool _isProcessing;
    private string _selectedFilePath = string.Empty;
    private string _documentName = "No document loaded";
    private string _pageCount = "--";
    private string _elapsedTime = "--";
    private string _meanConfidence = "--";
    private string _previewHeaderTitle = "Awaiting source document";
    private string _previewSubheading = "Preview pane";
    private string _previewPlaceholder = "Load a PDF or image to prepare the document stage. OCR overlays and rendered page surfaces will build on this panel in the next phase.";
    private string _summaryHeadline = "Document Intelligence Overview";
    private string _summaryNarrative = "Load a document and run OCR to replace these placeholders with real extraction metrics and diagnostics.";
    private string _jsonPreview = "{\n  \"status\": \"Awaiting OCR run\"\n}";
    private string _outputJsonPath = string.Empty;
    private bool _hasOcrResult;
    private BitmapImage? _previewImage;
    private bool _hasPreviewImage;
    private double _previewCanvasWidth = 1200;
    private double _previewCanvasHeight = 1600;
    private double _previewViewportWidth = 960;
    private double _previewViewportHeight = 720;
    private double _previewSurfaceWidth = 960;
    private double _previewSurfaceHeight = 720;
    private double _previewImageDisplayWidth = 540;
    private double _previewImageDisplayHeight = 720;
    private double _previewImageOffsetX = 210;
    private double _previewImageOffsetY;
    private double _previewZoom = 1.0;
    private bool _showWordOverlays;
    private bool _showFieldOverlays = true;
    private bool _showTableOverlays = true;
    private bool _hasSummaryData;
    private bool _hasFieldsData;
    private bool _hasTablesData;
    private bool _hasLogData = true;
    private bool _hasJsonData;
    private string? _lastOverlayDebugSignature;

    public MainWindowViewModel()
        : this(
            new InputFileDialogService(),
            new JsonSaveDialogService(),
            new OcrDemoService(new OcrProcessor()),
            new OverlayProjectionService(),
            new DemoStartupService())
    {
    }

    public MainWindowViewModel(
        IInputFileDialogService inputFileDialogService,
        IJsonSaveDialogService jsonSaveDialogService,
        IOcrDemoService ocrDemoService,
        IOverlayProjectionService overlayProjectionService,
        IDemoStartupService demoStartupService)
    {
        _inputFileDialogService = inputFileDialogService;
        _jsonSaveDialogService = jsonSaveDialogService;
        _ocrDemoService = ocrDemoService;
        _overlayProjectionService = overlayProjectionService;
        _demoStartupService = demoStartupService;

        LoadDocumentCommand = new RelayCommand(LoadDocument);
        RunOcrCommand = new RelayCommand(RunOcrAsync, CanRunOcr);
        ExportJsonCommand = new RelayCommand(ExportJson, CanExportJson);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        FitPreviewCommand = new RelayCommand(FitPreview);

        SummaryCards = [];
        SummaryDetails = [];
        LeftSummaryDetails = [];
        RightSummaryDetails = [];
        RecognizedFields = [];
        DetectedTables = [];
        LogEntries = [];
        VisibleOverlayItems = [];

        InitializeEmptyPresentationState();
        LoadStartupDemoIfAvailable();
    }

    public string ApplicationTitle => "OCR Showcase Demo";

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                RunOcrCommand.RaiseCanExecuteChanged();
                ExportJsonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedFilePath
    {
        get => _selectedFilePath;
        set
        {
            if (SetProperty(ref _selectedFilePath, value))
            {
                RunOcrCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DocumentName
    {
        get => _documentName;
        set => SetProperty(ref _documentName, value);
    }

    public string PageCount
    {
        get => _pageCount;
        set => SetProperty(ref _pageCount, value);
    }

    public string ElapsedTime
    {
        get => _elapsedTime;
        set => SetProperty(ref _elapsedTime, value);
    }

    public string MeanConfidence
    {
        get => _meanConfidence;
        set => SetProperty(ref _meanConfidence, value);
    }

    public string PreviewHeaderTitle
    {
        get => _previewHeaderTitle;
        set => SetProperty(ref _previewHeaderTitle, value);
    }

    public string PreviewSubheading
    {
        get => _previewSubheading;
        set => SetProperty(ref _previewSubheading, value);
    }

    public string PreviewPlaceholder
    {
        get => _previewPlaceholder;
        set => SetProperty(ref _previewPlaceholder, value);
    }

    public string SummaryHeadline
    {
        get => _summaryHeadline;
        set => SetProperty(ref _summaryHeadline, value);
    }

    public string SummaryNarrative
    {
        get => _summaryNarrative;
        set => SetProperty(ref _summaryNarrative, value);
    }

    public string JsonPreview
    {
        get => _jsonPreview;
        set
        {
            if (SetProperty(ref _jsonPreview, value))
            {
                ExportJsonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string OutputJsonPath
    {
        get => _outputJsonPath;
        set => SetProperty(ref _outputJsonPath, value);
    }

    public BitmapImage? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    public bool HasPreviewImage
    {
        get => _hasPreviewImage;
        set => SetProperty(ref _hasPreviewImage, value);
    }

    public double PreviewCanvasWidth
    {
        get => _previewCanvasWidth;
        set => SetProperty(ref _previewCanvasWidth, value);
    }

    public double PreviewCanvasHeight
    {
        get => _previewCanvasHeight;
        set => SetProperty(ref _previewCanvasHeight, value);
    }

    public double PreviewSurfaceWidth
    {
        get => _previewSurfaceWidth;
        set => SetProperty(ref _previewSurfaceWidth, value);
    }

    public double PreviewSurfaceHeight
    {
        get => _previewSurfaceHeight;
        set => SetProperty(ref _previewSurfaceHeight, value);
    }

    public double PreviewImageDisplayWidth
    {
        get => _previewImageDisplayWidth;
        set => SetProperty(ref _previewImageDisplayWidth, value);
    }

    public double PreviewImageDisplayHeight
    {
        get => _previewImageDisplayHeight;
        set => SetProperty(ref _previewImageDisplayHeight, value);
    }

    public double PreviewImageOffsetX
    {
        get => _previewImageOffsetX;
        set => SetProperty(ref _previewImageOffsetX, value);
    }

    public double PreviewImageOffsetY
    {
        get => _previewImageOffsetY;
        set => SetProperty(ref _previewImageOffsetY, value);
    }

    public double PreviewZoom
    {
        get => _previewZoom;
        set
        {
            var clamped = Math.Clamp(value, 0.75, 3.0);
            if (SetProperty(ref _previewZoom, clamped))
            {
                OnPropertyChanged(nameof(PreviewZoomText));
                RecalculatePreviewLayout();
            }
        }
    }

    public string PreviewZoomText => $"{PreviewZoom * 100:F0}%";

    public bool ShowWordOverlays
    {
        get => _showWordOverlays;
        set
        {
            if (SetProperty(ref _showWordOverlays, value))
            {
                RefreshVisibleOverlays();
            }
        }
    }

    public bool ShowFieldOverlays
    {
        get => _showFieldOverlays;
        set
        {
            if (SetProperty(ref _showFieldOverlays, value))
            {
                RefreshVisibleOverlays();
            }
        }
    }

    public bool ShowTableOverlays
    {
        get => _showTableOverlays;
        set
        {
            if (SetProperty(ref _showTableOverlays, value))
            {
                RefreshVisibleOverlays();
            }
        }
    }

    public bool HasSummaryData
    {
        get => _hasSummaryData;
        set => SetProperty(ref _hasSummaryData, value);
    }

    public bool HasFieldsData
    {
        get => _hasFieldsData;
        set => SetProperty(ref _hasFieldsData, value);
    }

    public bool HasTablesData
    {
        get => _hasTablesData;
        set => SetProperty(ref _hasTablesData, value);
    }

    public bool HasLogData
    {
        get => _hasLogData;
        set => SetProperty(ref _hasLogData, value);
    }

    public bool HasJsonData
    {
        get => _hasJsonData;
        set => SetProperty(ref _hasJsonData, value);
    }

    public ObservableCollection<SummaryCardViewModel> SummaryCards { get; }

    public ObservableCollection<SummaryDetailItemViewModel> SummaryDetails { get; }

    public ObservableCollection<SummaryDetailItemViewModel> LeftSummaryDetails { get; }

    public ObservableCollection<SummaryDetailItemViewModel> RightSummaryDetails { get; }

    public ObservableCollection<RecognizedFieldViewModel> RecognizedFields { get; }

    public ObservableCollection<DetectedTableViewModel> DetectedTables { get; }

    public ObservableCollection<LogEntryViewModel> LogEntries { get; }

    public ObservableCollection<PreviewOverlayItem> VisibleOverlayItems { get; }

    public RelayCommand LoadDocumentCommand { get; }

    public RelayCommand RunOcrCommand { get; }

    public RelayCommand ExportJsonCommand { get; }

    public RelayCommand ZoomInCommand { get; }

    public RelayCommand ZoomOutCommand { get; }

    public RelayCommand FitPreviewCommand { get; }

    public void UpdatePreviewViewportSize(double width, double height)
    {
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);

        if (Math.Abs(_previewViewportWidth - safeWidth) < 0.5 &&
            Math.Abs(_previewViewportHeight - safeHeight) < 0.5)
        {
            return;
        }

        _previewViewportWidth = safeWidth;
        _previewViewportHeight = safeHeight;
        RecalculatePreviewLayout();
    }

    private void LoadDocument()
    {
        var selectedFile = _inputFileDialogService.PickInputFile();
        if (string.IsNullOrWhiteSpace(selectedFile))
        {
            StatusMessage = "Document selection canceled.";
            return;
        }

        SelectedFilePath = selectedFile;
        DocumentName = Path.GetFileName(selectedFile);
        PageCount = string.Equals(Path.GetExtension(selectedFile), ".pdf", StringComparison.OrdinalIgnoreCase) ? "Pending OCR" : "1";
        ElapsedTime = "--";
        MeanConfidence = "--";
        OutputJsonPath = string.Empty;
        _hasOcrResult = false;
        HasJsonData = false;
        ExportJsonCommand.RaiseCanExecuteChanged();

        PreviewHeaderTitle = DocumentName;
        PreviewSubheading = selectedFile;
        PreviewPlaceholder = "Document loaded. Run OCR to generate summary metrics, machine-readable JSON, diagnostic logs, and live overlay boxes.";
        ClearPreviewSurface();

        SummaryHeadline = "Ready to run OCR";
        SummaryNarrative = $"The selected source is staged for processing: {DocumentName}. Execute the OCR pipeline to replace the placeholder metrics with live results from Ocr.Core.";
        JsonPreview = "{\n  \"status\": \"Document loaded. Run OCR to generate output.\"\n}";

        ResetCollectionsForNewSelection();
        AddLog("Input", $"Selected file: {selectedFile}");
        StatusMessage = $"Loaded {DocumentName}.";
    }

    private async Task RunOcrAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            StatusMessage = "Select a document before running OCR.";
            return;
        }

        IsProcessing = true;
        StatusMessage = "Running OCR pipeline...";
        AddLog("OCR", $"Starting OCR for {DocumentName}.");

        try
        {
            var runResult = await _ocrDemoService.RunAsync(SelectedFilePath);
            ApplyOcrResult(runResult);
            StatusMessage = $"OCR complete for {DocumentName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "OCR could not complete. Review the log tab for details.";
            _hasOcrResult = false;
            HasJsonData = false;
            ExportJsonCommand.RaiseCanExecuteChanged();
            AddLog("Error", ex.Message);
            JsonPreview = "{\n  \"status\": \"OCR failed\"\n}";
            ClearPreviewSurface();
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void ExportJson()
    {
        if (string.IsNullOrWhiteSpace(JsonPreview) || string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            StatusMessage = "Run OCR before exporting JSON.";
            return;
        }

        var suggestedFileName = BuildSuggestedJsonFileName();
        var initialDirectory = ResolveInitialJsonDirectory();
        var exportPath = _jsonSaveDialogService.PickSavePath(suggestedFileName, initialDirectory);
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            StatusMessage = "JSON export canceled.";
            return;
        }

        File.WriteAllText(exportPath, JsonPreview);
        OutputJsonPath = exportPath;
        StatusMessage = $"JSON exported to {exportPath}.";
        AddLog("Export", $"JSON written to {exportPath}");
    }

    private void ZoomIn() => PreviewZoom += 0.25;

    private void ZoomOut() => PreviewZoom -= 0.25;

    private void FitPreview() => PreviewZoom = 1.0;

    private bool CanRunOcr()
    {
        return !IsProcessing && !string.IsNullOrWhiteSpace(SelectedFilePath);
    }

    private bool CanExportJson()
    {
        return !IsProcessing && _hasOcrResult && !string.IsNullOrWhiteSpace(JsonPreview);
    }

    private string BuildSuggestedJsonFileName()
    {
        if (!string.IsNullOrWhiteSpace(OutputJsonPath))
        {
            return Path.GetFileName(OutputJsonPath);
        }

        if (!string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            return Path.GetFileNameWithoutExtension(SelectedFilePath) + ".ocr.json";
        }

        return "ocr-output.json";
    }

    private string? ResolveInitialJsonDirectory()
    {
        if (!string.IsNullOrWhiteSpace(OutputJsonPath))
        {
            return Path.GetDirectoryName(OutputJsonPath);
        }

        if (!string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            return Path.GetDirectoryName(SelectedFilePath);
        }

        return null;
    }

    private void ApplyOcrResult(OcrDemoRunResult runResult)
    {
        var contract = runResult.Contract;
        var totalTokens = contract.Pages.Sum(page => page.Tokens.Count);
        var meanConfidence = CalculateMeanConfidence(contract);
        var documentType = string.IsNullOrWhiteSpace(contract.Recognition.DocumentType.Name)
            ? "Unclassified"
            : contract.Recognition.DocumentType.Name!;

        OutputJsonPath = runResult.OutputJsonPath ?? string.Empty;
        _hasOcrResult = true;
        HasJsonData = true;
        ExportJsonCommand.RaiseCanExecuteChanged();
        JsonPreview = FormatJson(runResult.Json);
        DocumentName = contract.Document.Source.OriginalFileName;
        PageCount = contract.Document.Source.PageCount.ToString();
        ElapsedTime = FormatDuration(contract.Metrics.TotalMs);
        MeanConfidence = FormatPercent(meanConfidence);

        PreviewHeaderTitle = DocumentName;
        PreviewSubheading = string.IsNullOrWhiteSpace(SelectedFilePath) ? contract.Document.DocumentId : SelectedFilePath;
        PreviewPlaceholder = BuildPreviewMessage(contract);

        SummaryHeadline = $"OCR completed: {documentType}";
        SummaryNarrative = $"Processed {contract.Document.Source.PageCount} page(s), recognized {totalTokens} token(s), promoted {contract.Recognition.Fields.Count} field(s), and detected {contract.Pages.Sum(page => page.Tables.Count)} table(s).";

        PopulatePreviewSurface(contract);
        PopulateSummary(contract, documentType, totalTokens, meanConfidence);
        PopulateFields(contract);
        PopulateTables(contract);
        PopulateLog(contract, runResult.OutputJsonPath);
    }

    private void PopulatePreviewSurface(OcrContractRoot contract)
    {
        var projection = _overlayProjectionService.BuildPreviewProjection(contract);

        PreviewCanvasWidth = projection.Width;
        PreviewCanvasHeight = projection.Height;
        PreviewImage = LoadBitmap(projection.ImagePath);
        HasPreviewImage = PreviewImage is not null;

        _allOverlayItems.Clear();
        _allOverlayItems.AddRange(projection.OverlayItems);
        RecalculatePreviewLayout();
        FitPreview();
    }

    private void RefreshVisibleOverlays()
    {
        RecalculatePreviewLayout();
    }

    private bool ShouldShowOverlay(PreviewOverlayItem item)
    {
        return item.Kind switch
        {
            "Word" => ShowWordOverlays,
            "Field" => ShowFieldOverlays,
            "Table" => ShowTableOverlays,
            _ => false
        };
    }

    private void RecalculatePreviewLayout()
    {
        var imageWidth = Math.Max(1, PreviewCanvasWidth);
        var imageHeight = Math.Max(1, PreviewCanvasHeight);
        var viewportWidth = Math.Max(1, _previewViewportWidth);
        var viewportHeight = Math.Max(1, _previewViewportHeight);

        var fitScale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        if (double.IsNaN(fitScale) || double.IsInfinity(fitScale) || fitScale <= 0)
        {
            fitScale = 1.0;
        }

        var appliedScale = fitScale * PreviewZoom;
        var displayWidth = imageWidth * appliedScale;
        var displayHeight = imageHeight * appliedScale;
        var surfaceWidth = Math.Max(viewportWidth, displayWidth);
        var surfaceHeight = Math.Max(viewportHeight, displayHeight);
        var offsetX = Math.Max((surfaceWidth - displayWidth) / 2.0, 0);
        var offsetY = Math.Max((surfaceHeight - displayHeight) / 2.0, 0);

        PreviewSurfaceWidth = surfaceWidth;
        PreviewSurfaceHeight = surfaceHeight;
        PreviewImageDisplayWidth = displayWidth;
        PreviewImageDisplayHeight = displayHeight;
        PreviewImageOffsetX = offsetX;
        PreviewImageOffsetY = offsetY;

        VisibleOverlayItems.Clear();

        foreach (var item in _allOverlayItems.Where(ShouldShowOverlay))
        {
            VisibleOverlayItems.Add(item with
            {
                X = offsetX + (item.X * appliedScale),
                Y = offsetY + (item.Y * appliedScale),
                Width = item.Width * appliedScale,
                Height = item.Height * appliedScale
            });
        }

        if (EnableOverlayDebugLogging)
        {
            LogOverlayDebug(imageWidth, imageHeight, viewportWidth, viewportHeight, appliedScale, offsetX, offsetY);
        }
    }

    private void PopulateSummary(OcrContractRoot contract, string documentType, int totalTokens, double meanConfidence)
    {
        SummaryCards.Clear();
        SummaryDetails.Clear();

        var tableCount = contract.Pages.Sum(page => page.Tables.Count);
        var fieldCount = contract.Recognition.Fields.Count;
        var warningCount = contract.Warnings.Count;
        var errorCount = contract.Errors.Count;
        var breakdown = contract.Metrics.BreakdownMs;

        SummaryCards.Add(new SummaryCardViewModel("Document Type", documentType, "Recognition classification"));
        SummaryCards.Add(new SummaryCardViewModel("Pages", contract.Document.Source.PageCount.ToString(), "Pages processed"));
        SummaryCards.Add(new SummaryCardViewModel("Tokens", totalTokens.ToString(), "Words captured by OCR"));
        SummaryCards.Add(new SummaryCardViewModel("Confidence", FormatPercent(meanConfidence), "Average page confidence"));
        SummaryCards.Add(new SummaryCardViewModel("Tables", tableCount.ToString(), "Detected table regions"));
        SummaryCards.Add(new SummaryCardViewModel("Fields", fieldCount.ToString(), "Structured fields promoted"));

        SummaryDetails.Add(new SummaryDetailItemViewModel("Document", contract.Document.Source.OriginalFileName));
        SummaryDetails.Add(new SummaryDetailItemViewModel("File Type", contract.Document.Source.FileType));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Page Count", contract.Document.Source.PageCount.ToString()));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Engine", contract.Document.Processing.Engine.Name));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Total Time", FormatDuration(contract.Metrics.TotalMs)));
        SummaryDetails.Add(new SummaryDetailItemViewModel("OCR Time", FormatDuration(contract.Metrics.DocumentOcrMs)));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Render Time", FormatDuration(breakdown.RenderMs)));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Preprocess Time", FormatDuration(breakdown.PreprocessMs)));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Layout Time", FormatDuration(breakdown.LayoutMs)));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Recognition Time", FormatDuration(breakdown.RecognitionMs)));
        SummaryDetails.Add(new SummaryDetailItemViewModel(
            "Field Overlays",
            fieldCount == 0
                ? "None available. Show Fields only displays promoted structured field regions."
                : "Show Fields displays promoted structured field regions."));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Warnings", warningCount.ToString()));
        SummaryDetails.Add(new SummaryDetailItemViewModel("Errors", errorCount.ToString()));
        RebuildSummaryDetailColumns();

        HasSummaryData = SummaryCards.Count > 0 || SummaryDetails.Count > 0;
    }

    private void PopulateFields(OcrContractRoot contract)
    {
        RecognizedFields.Clear();

        foreach (var field in contract.Recognition.Fields
                     .OrderBy(field => field.Source.PageIndex)
                     .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase))
        {
            var value = field.Value?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                value = field.Normalized.Value?.ToString();
            }

            RecognizedFields.Add(new RecognizedFieldViewModel(
                string.IsNullOrWhiteSpace(field.Label) ? field.FieldId : field.Label,
                string.IsNullOrWhiteSpace(value) ? "(empty)" : value,
                FormatPercent(field.Confidence),
                field.Source.PageIndex > 0 ? field.Source.PageIndex.ToString() : "--"));
        }

        HasFieldsData = RecognizedFields.Count > 0;
    }

    private void PopulateTables(OcrContractRoot contract)
    {
        DetectedTables.Clear();

        foreach (var page in contract.Pages.OrderBy(page => page.PageIndex))
        {
            foreach (var table in page.Tables.OrderBy(table => table.TableId, StringComparer.OrdinalIgnoreCase))
            {
                DetectedTables.Add(BuildDetectedTableViewModel(table, page.PageIndex));
            }
        }

        HasTablesData = DetectedTables.Count > 0;
    }

    private void PopulateLog(OcrContractRoot contract, string? outputJsonPath)
    {
        LogEntries.Clear();

        AddLog("OCR", $"Completed processing for {contract.Document.Source.OriginalFileName}");
        AddLog("OCR", $"Pages: {contract.Document.Source.PageCount}, Tokens: {contract.Pages.Sum(page => page.Tokens.Count)}, Fields: {contract.Recognition.Fields.Count}");
        AddLog("Overlay", $"Preview overlays prepared: words={contract.Pages.Sum(page => page.Tokens.Count)}, fields={contract.Recognition.Fields.Count}, tables={contract.Pages.Sum(page => page.Tables.Count)}");

        if (!string.IsNullOrWhiteSpace(outputJsonPath))
        {
            AddLog("Export", $"Output JSON: {outputJsonPath}");
        }

        foreach (var stage in contract.Extensions.PipelineStageTimings)
        {
            var note = string.IsNullOrWhiteSpace(stage.Note) ? string.Empty : $" - {stage.Note}";
            AddLog("Pipeline", $"{stage.StageName}: {stage.DurationMs} ms ({stage.Status}){note}");
        }

        foreach (var warning in contract.Warnings)
        {
            AddLog("Warning", BuildIssueMessage(warning));
        }

        foreach (var error in contract.Errors)
        {
            AddLog("Error", BuildIssueMessage(error));
        }

        if (contract.Warnings.Count == 0 && contract.Errors.Count == 0)
        {
            AddLog("Health", "No warnings or errors were reported by the OCR contract.");
        }

        HasLogData = LogEntries.Count > 0;
    }

    private void ResetCollectionsForNewSelection()
    {
        SummaryCards.Clear();
        SummaryDetails.Clear();
        LeftSummaryDetails.Clear();
        RightSummaryDetails.Clear();
        RecognizedFields.Clear();
        DetectedTables.Clear();
        LogEntries.Clear();

        HasSummaryData = false;
        HasFieldsData = false;
        HasTablesData = false;
        HasLogData = false;
    }

    private void InitializeEmptyPresentationState()
    {
        ResetCollectionsForNewSelection();
        SummaryHeadline = "OCR Showcase Demo";
        SummaryNarrative = "A screenshot-ready review surface for OCR preview, extraction, JSON inspection, and diagnostics.";
        PreviewPlaceholder = "Load a PDF or image to begin, or let the app preload a saved sample OCR run for immediate presentation.";
        JsonPreview = "{\n  \"status\": \"Awaiting OCR run\"\n}";
        HasJsonData = false;
        StatusMessage = "Portfolio demo ready. Load a document or use the preloaded sample if available.";
        AddLog("System", "Ready.");
        HasLogData = true;
    }

    private void RebuildSummaryDetailColumns()
    {
        LeftSummaryDetails.Clear();
        RightSummaryDetails.Clear();

        for (var index = 0; index < SummaryDetails.Count; index++)
        {
            var targetColumn = index % 2 == 0 ? LeftSummaryDetails : RightSummaryDetails;
            targetColumn.Add(SummaryDetails[index]);
        }
    }

    private void ClearPreviewSurface()
    {
        PreviewImage = null;
        HasPreviewImage = false;
        PreviewCanvasWidth = 1200;
        PreviewCanvasHeight = 1600;
        PreviewSurfaceWidth = Math.Max(1, _previewViewportWidth);
        PreviewSurfaceHeight = Math.Max(1, _previewViewportHeight);
        PreviewImageDisplayWidth = 0;
        PreviewImageDisplayHeight = 0;
        PreviewImageOffsetX = 0;
        PreviewImageOffsetY = 0;
        _allOverlayItems.Clear();
        VisibleOverlayItems.Clear();
        FitPreview();
    }

    private static BitmapImage? LoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static DetectedTableViewModel BuildDetectedTableViewModel(TableInfo table, int pageIndex)
    {
        var rowsView = BuildTableRowsView(table, out var hasDisplayableGrid, out var emptyStateMessage);

        return new DetectedTableViewModel(
            table.TableId,
            pageIndex.ToString(),
            table.Detection.Method,
            FormatPercent(table.Confidence),
            $"{table.Grid.Rows} x {table.Grid.Cols}",
            rowsView,
            hasDisplayableGrid,
            emptyStateMessage);
    }

    private static DataView BuildTableRowsView(
        TableInfo table,
        out bool hasDisplayableGrid,
        out string emptyStateMessage)
    {
        var dataTable = new DataTable();

        if (table.Rows.Count == 0)
        {
            hasDisplayableGrid = false;
            emptyStateMessage = "The table region was detected, but no structured row values were emitted.";
            return dataTable.DefaultView;
        }

        var headerMappings = table.Header.Columns
            .OrderBy(column => column.ColIndex)
            .Select((column, index) => new
            {
                DisplayName = string.IsNullOrWhiteSpace(column.Name) ? $"Column {index + 1}" : column.Name.Trim(),
                ValueKey = string.IsNullOrWhiteSpace(column.Key) ? $"col_{index + 1}" : column.Key.Trim()
            })
            .Where(column => !string.IsNullOrWhiteSpace(column.DisplayName))
            .ToList();

        if (headerMappings.Count == 0)
        {
            headerMappings = table.Rows
                .SelectMany(row => row.Values.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Select((key, index) => new
                {
                    DisplayName = key,
                    ValueKey = key
                })
                .ToList();
        }

        if (headerMappings.Count == 0)
        {
            hasDisplayableGrid = false;
            emptyStateMessage = "The table was detected, but no displayable columns were available.";
            return dataTable.DefaultView;
        }

        var columnMappings = new List<(string DisplayName, string ValueKey, string ColumnName)>();
        for (var index = 0; index < headerMappings.Count; index++)
        {
            var header = headerMappings[index];
            var normalizedHeader = string.IsNullOrWhiteSpace(header.DisplayName) ? $"Column {index + 1}" : header.DisplayName;
            var columnName = normalizedHeader;
            var suffix = 2;

            while (dataTable.Columns.Contains(columnName))
            {
                columnName = $"{normalizedHeader} ({suffix++})";
            }

            dataTable.Columns.Add(columnName, typeof(string));
            columnMappings.Add((header.DisplayName, header.ValueKey, columnName));
        }

        var cellLookup = table.Cells
            .GroupBy(cell => (cell.RowIndex, cell.ColIndex))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(cell => !string.IsNullOrWhiteSpace(cell.Text))
                    .ThenByDescending(cell => cell.Confidence)
                    .First());

        var columnIndexByKey = table.Header.Columns
            .ToDictionary(
                column => string.IsNullOrWhiteSpace(column.Key) ? $"col_{column.ColIndex + 1}" : column.Key.Trim(),
                column => column.ColIndex,
                StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.Rows.OrderBy(row => row.RowIndex))
        {
            var dataRow = dataTable.NewRow();

            foreach (var (_, valueKey, columnName) in columnMappings)
            {
                if (columnIndexByKey.TryGetValue(valueKey, out var colIndex) &&
                    cellLookup.TryGetValue((row.RowIndex, colIndex), out var cell))
                {
                    var displayText = cell.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(displayText))
                    {
                        dataRow[columnName] = displayText;
                        continue;
                    }
                }

                if (!row.Values.TryGetValue(valueKey, out var value) || value is null)
                {
                    dataRow[columnName] = "--";
                    continue;
                }

                var text = value.ToString();
                dataRow[columnName] = string.IsNullOrWhiteSpace(text) ? "--" : text;
            }

            dataTable.Rows.Add(dataRow);
        }

        hasDisplayableGrid = true;
        emptyStateMessage = string.Empty;
        return dataTable.DefaultView;
    }

    private static double CalculateMeanConfidence(OcrContractRoot contract)
    {
        return contract.Pages.Count == 0
            ? 0
            : contract.Pages.Average(page => page.Quality.MeanTokenConfidence);
    }

    private static string BuildPreviewMessage(OcrContractRoot contract)
    {
        var artifactCount = contract.Extensions.DebugArtifactPaths.Count;
        if (artifactCount > 0)
        {
            return $"OCR completed. {artifactCount} preview artifact set(s) were generated. Use the overlay toggles to curate words, fields, and table regions for screenshots.";
        }

        return "OCR completed. Preview artifact generation was not available for this run, but the stage remains reserved for rendered pages and overlays.";
    }

    private static string BuildIssueMessage(IssueInfo issue)
    {
        var pageSuffix = issue.PageIndex.HasValue ? $" (page {issue.PageIndex.Value})" : string.Empty;
        return $"{issue.Code}{pageSuffix}: {issue.Message}";
    }

    private static string FormatDuration(int totalMilliseconds)
    {
        return totalMilliseconds <= 0 ? "--" : $"{totalMilliseconds / 1000.0:F2} s";
    }

    private static string FormatJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            var parsed = JsonConvert.DeserializeObject(json);
            return JsonConvert.SerializeObject(parsed, Formatting.Indented);
        }
        catch
        {
            return json;
        }
    }

    private static string FormatPercent(double value)
    {
        return value <= 0 ? "--" : $"{value * 100:F1}%";
    }

    private void AddLog(string source, string message)
    {
        LogEntries.Add(new LogEntryViewModel(DateTime.Now.ToString("HH:mm:ss"), source, message));
    }

    private void LogOverlayDebug(
        double imageWidth,
        double imageHeight,
        double viewportWidth,
        double viewportHeight,
        double scale,
        double offsetX,
        double offsetY)
    {
        var signature = $"{imageWidth:F0}|{imageHeight:F0}|{viewportWidth:F0}|{viewportHeight:F0}|{scale:F4}|{offsetX:F1}|{offsetY:F1}";
        if (string.Equals(signature, _lastOverlayDebugSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastOverlayDebugSignature = signature;
        AddLog("Overlay", $"Image size: {imageWidth:F0} x {imageHeight:F0}");
        AddLog("Overlay", $"Preview viewport: {viewportWidth:F0} x {viewportHeight:F0}");
        AddLog("Overlay", $"Scale: {scale:F4}");
        AddLog("Overlay", $"Offsets: x={offsetX:F1}, y={offsetY:F1}");
    }

    private void LoadStartupDemoIfAvailable()
    {
        var payload = _demoStartupService.TryLoadDemoStartup();
        if (payload is null)
        {
            return;
        }

        SelectedFilePath = payload.DisplaySourcePath;
        AddLog("Demo", $"Loaded startup sample from {payload.DisplaySourcePath}");
        ApplyOcrResult(payload.RunResult);
        StatusMessage = "Sample OCR presentation loaded for screenshots.";
    }
}

public sealed class SummaryCardViewModel
{
    public SummaryCardViewModel(string label, string value, string detail)
    {
        Label = label;
        Value = value;
        Detail = detail;
    }

    public string Label { get; }

    public string Value { get; }

    public string Detail { get; }
}

public sealed class SummaryDetailItemViewModel
{
    public SummaryDetailItemViewModel(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }

    public string Value { get; }
}

public sealed class RecognizedFieldViewModel
{
    public RecognizedFieldViewModel(string fieldName, string fieldValue, string confidence, string page)
    {
        FieldName = fieldName;
        FieldValue = fieldValue;
        Confidence = confidence;
        Page = page;
    }

    public string FieldName { get; }

    public string FieldValue { get; }

    public string Confidence { get; }

    public string Page { get; }
}

public sealed class DetectedTableViewModel
{
    public DetectedTableViewModel(
        string tableId,
        string page,
        string method,
        string confidence,
        string shape,
        DataView rowsView,
        bool hasDisplayableGrid,
        string emptyStateMessage)
    {
        TableId = tableId;
        Page = page;
        Method = method;
        Confidence = confidence;
        Shape = shape;
        RowsView = rowsView;
        HasDisplayableGrid = hasDisplayableGrid;
        EmptyStateMessage = emptyStateMessage;
    }

    public string TableId { get; }

    public string Page { get; }

    public string Method { get; }

    public string Confidence { get; }

    public string Shape { get; }

    public DataView RowsView { get; }

    public bool HasDisplayableGrid { get; }

    public string EmptyStateMessage { get; }
}

public sealed class LogEntryViewModel
{
    public LogEntryViewModel(string time, string source, string message)
    {
        Time = time;
        Source = source;
        Message = message;
    }

    public string Time { get; }

    public string Source { get; }

    public string Message { get; }
}
