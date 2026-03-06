using System.Collections.ObjectModel;
using System.Dynamic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using Ocr.Core.Abstractions;
using Ocr.Core.Models;
using Ocr.Core.Services;
using Ocr.TestHarness.Wpf.Commands;
using WinForms = System.Windows.Forms;

namespace Ocr.TestHarness.Wpf.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly IOcrProcessor _ocrProcessor;
    private string _selectedFilePath = string.Empty;
    private string _jsonOutput = string.Empty;
    private string _outputJsonPath = string.Empty;
    private bool _isBusy;
    private BitmapImage? _previewImage;
    private string _previewStatusMessage = "Run OCR to generate preview assets.";
    private int _selectedPreviewPage;
    private string _selectedPreviewView = "Original/Rendered";
    private double _previewZoom = 1.0;
    private double _previewViewportWidth;
    private double _previewViewportHeight;
    private string _summaryFileName = string.Empty;
    private string _summaryFileType = string.Empty;
    private int _summaryPageCount;
    private string _summaryOutputJsonPath = string.Empty;
    private int _summaryTotalMs;
    private int _summaryDocumentOcrMs;
    private int _summaryRenderMs;
    private int _summaryPreprocessMs;
    private int _summaryLayoutMs;
    private int _summaryOcrMs;
    private int _summaryTokenCount;
    private double _summaryMeanConfidence;
    private int _summaryLowConfidenceTokenCount;
    private int _summaryWarningCount;
    private int _summaryErrorCount;
    private int _summaryTableCount;
    private double _summaryMeanTableConfidence;
    private string _summaryTableMethods = string.Empty;
    private string _summaryHighestConfidenceTable = string.Empty;
    private int _summaryCheckboxCount;
    private int _summaryRadioCount;
    private int _summaryCheckedRegionCount;
    private int _summaryUniqueWordCount;
    private int _summaryKeyValueCandidateCount;
    private int _summaryPromotedFieldCount;
    private TableSummaryItem? _selectedTableSummary;
    private string _selectedTableId = string.Empty;
    private int _selectedTablePageIndex;
    private string _selectedTableMethod = string.Empty;
    private bool _selectedTableHasExplicitGridLines;
    private double _selectedTableConfidence;
    private string _selectedTableBboxText = string.Empty;
    private double _selectedTableTokenCoverageRatio;
    private string _selectedTableOverlayPath = string.Empty;
    private string _tablesStatusMessage = "Run OCR to populate table review.";
    private string _selectedWordScope = "All";
    private int _displayWordCount;

    private int _targetDpi = 300;
    private string _language = "eng";
    private int? _pageSegMode;
    private int? _engineMode;
    private bool _preserveInterwordSpaces = true;
    private bool _saveTokenOverlay;
    private bool _enableDeskew = true;
    private double _maxDeskewDegrees = 40.0;
    private double _deskewAngleStep = 0.5;
    private double _minDeskewConfidence = 0.15;
    private bool _enableDenoise;
    private string _denoiseMethod = "median";
    private int _denoiseKernel = 3;
    private bool _enableBinarization;
    private string _binarizationMethod = "otsu";
    private bool _enableContrastEnhancement;
    private string _contrastMethod = "clahe";
    private bool _saveJsonToDisk = true;
    private string _outputFolder = string.Empty;
    private bool _saveDebugArtifacts;
    private bool _enableNoiseFiltering;
    private string _profileName = "default";
    private readonly Dictionary<int, PageArtifactSet> _previewArtifactsByPage = [];
    private readonly Dictionary<string, JObject> _tableJsonByKey = [];
    private readonly Dictionary<int, string> _tableOverlayPathByPage = [];
    private readonly Dictionary<int, List<string>> _pageWordsByPageIndex = [];
    private readonly List<string> _documentWords = [];

    public MainViewModel()
        : this(new OcrProcessor())
    {
    }

    public MainViewModel(IOcrProcessor ocrProcessor)
    {
        _ocrProcessor = ocrProcessor;

        BrowseFileCommand = new RelayCommand(BrowseFile);
        BrowseOutputFolderCommand = new RelayCommand(BrowseOutputFolder);
        RunOcrCommand = new RelayCommand(RunOcrAsync, CanRunOcr);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        FitToWindowCommand = new RelayCommand(FitToWindow);

        PreviewViewOptions.Add("Original/Rendered");
        PreviewViewOptions.Add("Grayscale");
        PreviewViewOptions.Add("Preprocessed");
        PreviewViewOptions.Add("Overlay");
        PreviewViewOptions.Add("Lines Overlay");
        PreviewViewOptions.Add("Blocks Overlay");
        PreviewViewOptions.Add("Table Overlay");
        PreviewViewOptions.Add("Region Overlay");
        WordScopeOptions.Add("All");

        LogLines.Add("Ready.");
    }

    public RelayCommand BrowseFileCommand { get; }
    public RelayCommand BrowseOutputFolderCommand { get; }
    public RelayCommand RunOcrCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand FitToWindowCommand { get; }
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<int> PreviewPages { get; } = [];
    public ObservableCollection<string> PreviewViewOptions { get; } = [];
    public ObservableCollection<PageSummaryItem> PageSummaries { get; } = [];
    public ObservableCollection<FieldSummaryItem> FieldSummaries { get; } = [];
    public ObservableCollection<string> DisplayWords { get; } = [];
    public ObservableCollection<string> WordScopeOptions { get; } = [];
    public ObservableCollection<TableSummaryItem> TableSummaries { get; } = [];
    public ObservableCollection<TableHeaderColumnItem> SelectedTableHeaderColumns { get; } = [];
    public ObservableCollection<TableCellDisplayItem> SelectedTableHeaderCells { get; } = [];
    public ObservableCollection<TableCellDisplayItem> SelectedTableRawCells { get; } = [];
    public ObservableCollection<ExpandoObject> SelectedTableDisplayRows { get; } = [];

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

    public string JsonOutput
    {
        get => _jsonOutput;
        set => SetProperty(ref _jsonOutput, value);
    }

    public string OutputJsonPath
    {
        get => _outputJsonPath;
        set => SetProperty(ref _outputJsonPath, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(RunButtonText));
                RunOcrCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RunButtonText => IsBusy ? "Running..." : "Run";

    public int TargetDpi
    {
        get => _targetDpi;
        set => SetProperty(ref _targetDpi, value);
    }

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value);
    }

    public bool EnableDeskew
    {
        get => _enableDeskew;
        set => SetProperty(ref _enableDeskew, value);
    }

    public int? PageSegMode
    {
        get => _pageSegMode;
        set => SetProperty(ref _pageSegMode, value);
    }

    public int? EngineMode
    {
        get => _engineMode;
        set => SetProperty(ref _engineMode, value);
    }

    public bool PreserveInterwordSpaces
    {
        get => _preserveInterwordSpaces;
        set => SetProperty(ref _preserveInterwordSpaces, value);
    }

    public bool SaveTokenOverlay
    {
        get => _saveTokenOverlay;
        set => SetProperty(ref _saveTokenOverlay, value);
    }

    public double MaxDeskewDegrees
    {
        get => _maxDeskewDegrees;
        set => SetProperty(ref _maxDeskewDegrees, value);
    }

    public double DeskewAngleStep
    {
        get => _deskewAngleStep;
        set => SetProperty(ref _deskewAngleStep, value);
    }

    public double MinDeskewConfidence
    {
        get => _minDeskewConfidence;
        set => SetProperty(ref _minDeskewConfidence, value);
    }

    public bool EnableDenoise
    {
        get => _enableDenoise;
        set => SetProperty(ref _enableDenoise, value);
    }

    public string DenoiseMethod
    {
        get => _denoiseMethod;
        set => SetProperty(ref _denoiseMethod, value);
    }

    public int DenoiseKernel
    {
        get => _denoiseKernel;
        set => SetProperty(ref _denoiseKernel, value);
    }

    public bool EnableBinarization
    {
        get => _enableBinarization;
        set => SetProperty(ref _enableBinarization, value);
    }

    public string BinarizationMethod
    {
        get => _binarizationMethod;
        set => SetProperty(ref _binarizationMethod, value);
    }

    public bool EnableContrastEnhancement
    {
        get => _enableContrastEnhancement;
        set => SetProperty(ref _enableContrastEnhancement, value);
    }

    public string ContrastMethod
    {
        get => _contrastMethod;
        set => SetProperty(ref _contrastMethod, value);
    }

    public bool SaveJsonToDisk
    {
        get => _saveJsonToDisk;
        set => SetProperty(ref _saveJsonToDisk, value);
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set => SetProperty(ref _outputFolder, value);
    }

    public bool SaveDebugArtifacts
    {
        get => _saveDebugArtifacts;
        set => SetProperty(ref _saveDebugArtifacts, value);
    }

    public bool EnableNoiseFiltering
    {
        get => _enableNoiseFiltering;
        set => SetProperty(ref _enableNoiseFiltering, value);
    }

    public BitmapImage? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    public string PreviewStatusMessage
    {
        get => _previewStatusMessage;
        set => SetProperty(ref _previewStatusMessage, value);
    }

    public int SelectedPreviewPage
    {
        get => _selectedPreviewPage;
        set
        {
            if (SetProperty(ref _selectedPreviewPage, value))
            {
                UpdatePreviewImage();
            }
        }
    }

    public string SelectedPreviewView
    {
        get => _selectedPreviewView;
        set
        {
            if (SetProperty(ref _selectedPreviewView, value))
            {
                UpdatePreviewImage();
            }
        }
    }

    public double PreviewZoom
    {
        get => _previewZoom;
        set => SetProperty(ref _previewZoom, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public string SummaryFileName
    {
        get => _summaryFileName;
        set => SetProperty(ref _summaryFileName, value);
    }

    public string SummaryFileType
    {
        get => _summaryFileType;
        set => SetProperty(ref _summaryFileType, value);
    }

    public int SummaryPageCount
    {
        get => _summaryPageCount;
        set => SetProperty(ref _summaryPageCount, value);
    }

    public string SummaryOutputJsonPath
    {
        get => _summaryOutputJsonPath;
        set => SetProperty(ref _summaryOutputJsonPath, value);
    }

    public int SummaryTotalMs
    {
        get => _summaryTotalMs;
        set => SetProperty(ref _summaryTotalMs, value);
    }

    public int SummaryDocumentOcrMs
    {
        get => _summaryDocumentOcrMs;
        set => SetProperty(ref _summaryDocumentOcrMs, value);
    }

    public int SummaryRenderMs
    {
        get => _summaryRenderMs;
        set => SetProperty(ref _summaryRenderMs, value);
    }

    public int SummaryPreprocessMs
    {
        get => _summaryPreprocessMs;
        set => SetProperty(ref _summaryPreprocessMs, value);
    }

    public int SummaryLayoutMs
    {
        get => _summaryLayoutMs;
        set => SetProperty(ref _summaryLayoutMs, value);
    }

    public int SummaryOcrMs
    {
        get => _summaryOcrMs;
        set => SetProperty(ref _summaryOcrMs, value);
    }

    public int SummaryTokenCount
    {
        get => _summaryTokenCount;
        set => SetProperty(ref _summaryTokenCount, value);
    }

    public double SummaryMeanConfidence
    {
        get => _summaryMeanConfidence;
        set => SetProperty(ref _summaryMeanConfidence, value);
    }

    public int SummaryLowConfidenceTokenCount
    {
        get => _summaryLowConfidenceTokenCount;
        set => SetProperty(ref _summaryLowConfidenceTokenCount, value);
    }

    public int SummaryWarningCount
    {
        get => _summaryWarningCount;
        set => SetProperty(ref _summaryWarningCount, value);
    }

    public int SummaryErrorCount
    {
        get => _summaryErrorCount;
        set => SetProperty(ref _summaryErrorCount, value);
    }

    public int SummaryTableCount
    {
        get => _summaryTableCount;
        set => SetProperty(ref _summaryTableCount, value);
    }

    public double SummaryMeanTableConfidence
    {
        get => _summaryMeanTableConfidence;
        set => SetProperty(ref _summaryMeanTableConfidence, value);
    }

    public string SummaryTableMethods
    {
        get => _summaryTableMethods;
        set => SetProperty(ref _summaryTableMethods, value);
    }

    public string SummaryHighestConfidenceTable
    {
        get => _summaryHighestConfidenceTable;
        set => SetProperty(ref _summaryHighestConfidenceTable, value);
    }

    public int SummaryCheckboxCount
    {
        get => _summaryCheckboxCount;
        set => SetProperty(ref _summaryCheckboxCount, value);
    }

    public int SummaryRadioCount
    {
        get => _summaryRadioCount;
        set => SetProperty(ref _summaryRadioCount, value);
    }

    public int SummaryCheckedRegionCount
    {
        get => _summaryCheckedRegionCount;
        set => SetProperty(ref _summaryCheckedRegionCount, value);
    }

    public int SummaryUniqueWordCount
    {
        get => _summaryUniqueWordCount;
        set => SetProperty(ref _summaryUniqueWordCount, value);
    }

    public int SummaryKeyValueCandidateCount
    {
        get => _summaryKeyValueCandidateCount;
        set => SetProperty(ref _summaryKeyValueCandidateCount, value);
    }

    public int SummaryPromotedFieldCount
    {
        get => _summaryPromotedFieldCount;
        set => SetProperty(ref _summaryPromotedFieldCount, value);
    }

    public TableSummaryItem? SelectedTableSummary
    {
        get => _selectedTableSummary;
        set
        {
            if (SetProperty(ref _selectedTableSummary, value))
            {
                LoadSelectedTableDetails();
            }
        }
    }

    public string SelectedTableId
    {
        get => _selectedTableId;
        set => SetProperty(ref _selectedTableId, value);
    }

    public int SelectedTablePageIndex
    {
        get => _selectedTablePageIndex;
        set => SetProperty(ref _selectedTablePageIndex, value);
    }

    public string SelectedTableMethod
    {
        get => _selectedTableMethod;
        set => SetProperty(ref _selectedTableMethod, value);
    }

    public bool SelectedTableHasExplicitGridLines
    {
        get => _selectedTableHasExplicitGridLines;
        set => SetProperty(ref _selectedTableHasExplicitGridLines, value);
    }

    public double SelectedTableConfidence
    {
        get => _selectedTableConfidence;
        set => SetProperty(ref _selectedTableConfidence, value);
    }

    public string SelectedTableBboxText
    {
        get => _selectedTableBboxText;
        set => SetProperty(ref _selectedTableBboxText, value);
    }

    public double SelectedTableTokenCoverageRatio
    {
        get => _selectedTableTokenCoverageRatio;
        set => SetProperty(ref _selectedTableTokenCoverageRatio, value);
    }

    public string SelectedTableOverlayPath
    {
        get => _selectedTableOverlayPath;
        set => SetProperty(ref _selectedTableOverlayPath, value);
    }

    public string TablesStatusMessage
    {
        get => _tablesStatusMessage;
        set => SetProperty(ref _tablesStatusMessage, value);
    }

    public string SelectedWordScope
    {
        get => _selectedWordScope;
        set
        {
            var safeValue = string.IsNullOrWhiteSpace(value) ? "All" : value;
            if (SetProperty(ref _selectedWordScope, safeValue))
            {
                UpdateDisplayWordsForScope();
            }
        }
    }

    public int DisplayWordCount
    {
        get => _displayWordCount;
        set => SetProperty(ref _displayWordCount, value);
    }

    private bool CanRunOcr()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(SelectedFilePath);
    }

    private void BrowseFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Supported files|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.gif;*.bmp|PDF files|*.pdf|Image files|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.gif;*.bmp|All files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedFilePath = dialog.FileName;
            AddLog($"Selected file: {SelectedFilePath}");
        }
    }

    private void BrowseOutputFolder()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select output folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            OutputFolder = dialog.SelectedPath;
            AddLog($"Selected output folder: {OutputFolder}");
        }
    }

    private async Task RunOcrAsync()
    {
        IsBusy = true;

        try
        {
            AddLog("Starting OCR processor.");

            var options = new OcrOptions
            {
                TargetDpi = TargetDpi,
                Language = Language,
                PageSegMode = PageSegMode,
                EngineMode = EngineMode,
                PreserveInterwordSpaces = PreserveInterwordSpaces,
                SaveTokenOverlay = SaveTokenOverlay,
                EnableDeskew = EnableDeskew,
                MaxDeskewDegrees = MaxDeskewDegrees,
                DeskewAngleStep = DeskewAngleStep,
                MinDeskewConfidence = MinDeskewConfidence,
                EnableDenoise = EnableDenoise,
                DenoiseMethod = DenoiseMethod,
                DenoiseKernel = DenoiseKernel,
                EnableBinarization = EnableBinarization,
                BinarizationMethod = BinarizationMethod,
                EnableContrastEnhancement = EnableContrastEnhancement,
                ContrastMethod = ContrastMethod,
                SaveJsonToDisk = SaveJsonToDisk,
                OutputFolder = string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder,
                SaveDebugArtifacts = SaveDebugArtifacts,
                EnableNoiseFiltering = EnableNoiseFiltering,
                ProfileName = string.IsNullOrWhiteSpace(ProfileName) ? "default" : ProfileName
            };

            var result = await Task.Run(() => _ocrProcessor.ProcessFile(SelectedFilePath, options));

            JsonOutput = result.Json;
            OutputJsonPath = result.OutputJsonPath ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(OutputJsonPath))
            {
                AddLog($"Output path: {OutputJsonPath}");
            }
            else
            {
                AddLog("Output path: (not written)");
            }

            var (warningCount, errorCount) = CountWarningsAndErrors(result.Json);
            UpdateSummary(result.Json, result.OutputJsonPath);
            LogPageSummary(result.Json);
            LogNoiseDiagnostics(result.Json);
            LogDebugArtifacts(result.Json);
            PopulatePreviewArtifacts(result.Json);
            PopulateTables(result.Json);
            PopulateWords(result.Json);
            AddLog($"Warnings: {warningCount}, Errors: {errorCount}");
        }
        catch (Exception ex)
        {
            AddLog($"Failed: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"Run failed: {ex.Message}",
                "OCR Test Harness",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (int warnings, int errors) CountWarningsAndErrors(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (0, 0);
        }

        try
        {
            var jObject = JObject.Parse(json);
            var warnings = jObject["warnings"] as JArray;
            var errors = jObject["errors"] as JArray;
            return (warnings?.Count ?? 0, errors?.Count ?? 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void UpdateSummary(string json, string? outputJsonPath)
    {
        SummaryOutputJsonPath = outputJsonPath ?? string.Empty;
        PageSummaries.Clear();
        FieldSummaries.Clear();

        if (string.IsNullOrWhiteSpace(json))
        {
            SummaryFileName = string.Empty;
            SummaryFileType = string.Empty;
            SummaryPageCount = 0;
            SummaryTotalMs = 0;
            SummaryDocumentOcrMs = 0;
            SummaryRenderMs = 0;
            SummaryPreprocessMs = 0;
            SummaryLayoutMs = 0;
            SummaryOcrMs = 0;
            SummaryTokenCount = 0;
            SummaryMeanConfidence = 0;
            SummaryLowConfidenceTokenCount = 0;
            SummaryWarningCount = 0;
            SummaryErrorCount = 0;
            SummaryTableMethods = string.Empty;
            SummaryHighestConfidenceTable = string.Empty;
            SummaryCheckboxCount = 0;
            SummaryRadioCount = 0;
            SummaryCheckedRegionCount = 0;
            SummaryUniqueWordCount = 0;
            SummaryKeyValueCandidateCount = 0;
            SummaryPromotedFieldCount = 0;
            return;
        }

        try
        {
            var root = JObject.Parse(json);
            SummaryFileName = root["document"]?["source"]?["originalFileName"]?.Value<string>() ?? string.Empty;
            SummaryFileType = root["document"]?["source"]?["fileType"]?.Value<string>() ?? string.Empty;
            SummaryPageCount = root["document"]?["source"]?["pageCount"]?.Value<int>() ?? 0;
            SummaryTotalMs = root["metrics"]?["totalMs"]?.Value<int>() ?? 0;
            SummaryDocumentOcrMs = root["metrics"]?["documentOcrMs"]?.Value<int>() ?? 0;
            SummaryRenderMs = root["metrics"]?["breakdownMs"]?["renderMs"]?.Value<int>() ?? 0;
            SummaryPreprocessMs = root["metrics"]?["breakdownMs"]?["preprocessMs"]?.Value<int>() ?? 0;
            SummaryLayoutMs = root["metrics"]?["breakdownMs"]?["layoutMs"]?.Value<int>() ?? 0;
            SummaryOcrMs = root["metrics"]?["breakdownMs"]?["ocrMs"]?.Value<int>() ?? 0;
            SummaryWarningCount = (root["warnings"] as JArray)?.Count ?? 0;
            SummaryErrorCount = (root["errors"] as JArray)?.Count ?? 0;
            SummaryUniqueWordCount = (root["documentWords"] as JArray)?.Count ?? 0;
            var fields = root["recognition"]?["fields"] as JArray;
            SummaryPromotedFieldCount = fields?.Count ?? 0;
            if (fields is not null)
            {
                foreach (var field in fields.OfType<JObject>())
                {
                    FieldSummaries.Add(new FieldSummaryItem
                    {
                        FieldId = field["fieldId"]?.Value<string>() ?? string.Empty,
                        Label = field["label"]?.Value<string>() ?? string.Empty,
                        Value = field["value"]?.Value<string>() ?? field["normalized"]?["value"]?.ToString() ?? string.Empty,
                        Confidence = field["confidence"]?.Value<double>() ?? 0
                    });
                }
            }

            var pages = root["pages"] as JArray;
            if (pages is null || pages.Count == 0)
            {
                SummaryTokenCount = 0;
                SummaryMeanConfidence = 0;
                SummaryLowConfidenceTokenCount = 0;
                SummaryCheckboxCount = 0;
                SummaryRadioCount = 0;
                SummaryCheckedRegionCount = 0;
                return;
            }

            var totalTokenCount = 0;
            var totalLowConfidenceCount = 0;
            var totalConfidence = 0.0;
            var totalKeyValueCandidates = 0;
            var totalCheckboxes = 0;
            var totalRadios = 0;
            var totalCheckedRegions = 0;

            foreach (var page in pages.OfType<JObject>())
            {
                var pageIndex = page["pageIndex"]?.Value<int>() ?? 0;
                var tokens = page["tokens"] as JArray;
                var tokenCount = tokens?.Count ?? 0;
                var meanConfidence = page["quality"]?["meanTokenConfidence"]?.Value<double>() ?? 0;
                var lowConfidenceCount = page["quality"]?["lowConfidenceTokenCount"]?.Value<int>() ?? 0;
                var renderMs = page["timing"]?["renderMs"]?.Value<int>() ?? 0;
                var preprocessMs = page["timing"]?["preprocessMs"]?.Value<int>() ?? 0;
                var ocrMs = page["timing"]?["ocrMs"]?.Value<int>() ?? 0;
                var layoutMs = page["timing"]?["layoutMs"]?.Value<int>() ?? 0;
                var tables = page["tables"] as JArray;
                var tableCount = tables?.Count ?? 0;
                var tableConfidence = tableCount == 0
                    ? 0
                    : (tables?.OfType<JObject>().Average(t => t["confidence"]?.Value<double>() ?? 0) ?? 0);
                var keyValueCandidates = page["keyValueCandidates"] as JArray;
                totalKeyValueCandidates += keyValueCandidates?.Count ?? 0;
                var regions = page["regions"] as JArray;
                var checkboxCount = regions?.OfType<JObject>().Count(r => string.Equals(r["type"]?.Value<string>(), "checkbox", StringComparison.OrdinalIgnoreCase)) ?? 0;
                var radioCount = regions?.OfType<JObject>().Count(r => string.Equals(r["type"]?.Value<string>(), "radio", StringComparison.OrdinalIgnoreCase)) ?? 0;
                var checkedCount = regions?.OfType<JObject>().Count(r => r["value"]?.Value<bool>() == true) ?? 0;

                PageSummaries.Add(new PageSummaryItem
                {
                    PageIndex = pageIndex,
                    TokenCount = tokenCount,
                    MeanConfidence = meanConfidence,
                    RenderMs = renderMs,
                    PreprocessMs = preprocessMs,
                    OcrMs = ocrMs,
                    LayoutMs = layoutMs,
                    TableCount = tableCount,
                    MeanTableConfidence = tableConfidence,
                    CheckboxCount = checkboxCount,
                    RadioCount = radioCount,
                    CheckedRegionCount = checkedCount
                });

                totalTokenCount += tokenCount;
                totalLowConfidenceCount += lowConfidenceCount;
                totalConfidence += meanConfidence;
                totalCheckboxes += checkboxCount;
                totalRadios += radioCount;
                totalCheckedRegions += checkedCount;
            }

            SummaryTokenCount = totalTokenCount;
            SummaryLowConfidenceTokenCount = totalLowConfidenceCount;
            SummaryMeanConfidence = pages.Count == 0 ? 0 : totalConfidence / pages.Count;
            SummaryTableCount = PageSummaries.Sum(p => p.TableCount);
            SummaryMeanTableConfidence = PageSummaries.Count == 0 ? 0 : PageSummaries.Average(p => p.MeanTableConfidence);
            SummaryKeyValueCandidateCount = totalKeyValueCandidates;
            var methods = pages
                .OfType<JObject>()
                .SelectMany(p => (p["tables"] as JArray)?.OfType<JObject>() ?? [])
                .Select(t => t["detection"]?["method"]?.Value<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
            SummaryTableMethods = methods.Count == 0 ? string.Empty : string.Join(", ", methods);
            var bestTable = pages
                .OfType<JObject>()
                .SelectMany(p => (p["tables"] as JArray)?.OfType<JObject>() ?? [])
                .OrderByDescending(t => t["confidence"]?.Value<double>() ?? 0)
                .ThenBy(t => t["pageIndex"]?.Value<int>() ?? 0)
                .FirstOrDefault();
            SummaryHighestConfidenceTable = bestTable is null
                ? string.Empty
                : $"{bestTable["tableId"]?.Value<string>()} ({(bestTable["confidence"]?.Value<double>() ?? 0):F3})";
            SummaryCheckboxCount = totalCheckboxes;
            SummaryRadioCount = totalRadios;
            SummaryCheckedRegionCount = totalCheckedRegions;
        }
        catch
        {
            SummaryFileName = string.Empty;
            SummaryFileType = string.Empty;
            SummaryPageCount = 0;
            SummaryTableCount = 0;
            SummaryMeanTableConfidence = 0;
            SummaryTableMethods = string.Empty;
            SummaryHighestConfidenceTable = string.Empty;
            SummaryCheckboxCount = 0;
            SummaryRadioCount = 0;
            SummaryCheckedRegionCount = 0;
            SummaryUniqueWordCount = 0;
            SummaryKeyValueCandidateCount = 0;
            SummaryPromotedFieldCount = 0;
        }
    }

    private void LogPageSummary(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var root = JObject.Parse(json);
            var pages = root["pages"] as JArray;
            var pageCount = pages?.Count ?? 0;
            AddLog($"Page count: {pageCount}");
            var documentOcrMs = root["metrics"]?["documentOcrMs"]?.Value<int>() ?? 0;
            var totalMs = root["metrics"]?["totalMs"]?.Value<int>() ?? 0;
            AddLog($"documentOcrMs: {documentOcrMs}");
            AddLog($"totalMs: {totalMs}");

            var fileType = root["document"]?["source"]?["fileType"]?.Value<string>();
            if (string.Equals(fileType, "pdf", StringComparison.OrdinalIgnoreCase))
            {
                var declaredPageCount = root["document"]?["source"]?["pageCount"]?.Value<int>() ?? 0;
                var renderMs = root["metrics"]?["breakdownMs"]?["renderMs"]?.Value<int>() ?? 0;
                var hasRenderError = (root["errors"] as JArray)?.OfType<JObject>()
                    .Any(e => string.Equals(e["code"]?.Value<string>(), "pdf_render_failed", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(e["code"]?.Value<string>(), "pdf_page_count_failed", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(e["code"]?.Value<string>(), "ghostscript_not_found", StringComparison.OrdinalIgnoreCase)) ?? false;
                var renderSucceeded = !hasRenderError && pageCount > 0 && declaredPageCount == pageCount;
                AddLog($"PDF page count: {declaredPageCount}");
                AddLog($"PDF render succeeded: {renderSucceeded} (renderMs={renderMs})");
            }

            if (pages is null)
            {
                return;
            }

            var totalCheckboxes = 0;
            var totalRadios = 0;
            var totalChecked = 0;

            foreach (var page in pages.OfType<JObject>())
            {
                var pageIndex = page["pageIndex"]?.Value<int>() ?? 0;
                var timing = page["timing"] as JObject;
                if (timing is null)
                {
                    continue;
                }

                var renderMs = timing["renderMs"]?.Value<int>() ?? 0;
                var preprocessMs = timing["preprocessMs"]?.Value<int>() ?? 0;
                var ocrMs = timing["ocrMs"]?.Value<int>() ?? 0;
                var layoutMs = timing["layoutMs"]?.Value<int>() ?? 0;
                var postprocessMs = timing["postprocessMs"]?.Value<int>() ?? 0;
                var tableCount = (page["tables"] as JArray)?.Count ?? 0;
                var keyValueCount = (page["keyValueCandidates"] as JArray)?.Count ?? 0;
                var regions = page["regions"] as JArray;
                var checkboxCount = regions?.OfType<JObject>().Count(r => string.Equals(r["type"]?.Value<string>(), "checkbox", StringComparison.OrdinalIgnoreCase)) ?? 0;
                var radioCount = regions?.OfType<JObject>().Count(r => string.Equals(r["type"]?.Value<string>(), "radio", StringComparison.OrdinalIgnoreCase)) ?? 0;
                var checkedCount = regions?.OfType<JObject>().Count(r => r["value"]?.Value<bool>() == true) ?? 0;
                AddLog($"Page {pageIndex} timing ms: render={renderMs}, preprocess={preprocessMs}, ocr={ocrMs}, layout={layoutMs}, postprocess={postprocessMs}");
                AddLog($"Page {pageIndex} tables: {tableCount}");
                AddLog($"Page {pageIndex} key-value candidates: {keyValueCount}");
                AddLog($"Page {pageIndex} regions: checkboxes={checkboxCount}, radios={radioCount}, checked={checkedCount}");
                totalCheckboxes += checkboxCount;
                totalRadios += radioCount;
                totalChecked += checkedCount;
                var tableEntries = (page["tables"] as JArray)?.OfType<JObject>() ?? [];
                foreach (var table in tableEntries)
                {
                    var tableId = table["tableId"]?.Value<string>() ?? "tbl";
                    var method = table["detection"]?["method"]?.Value<string>() ?? "unknown";
                    var confidence = table["confidence"]?.Value<double>() ?? 0;
                    AddLog($"Page {pageIndex} table {tableId}: method={method}, confidence={confidence:F3}");

                    if (method.Contains("lines", StringComparison.OrdinalIgnoreCase))
                    {
                        var rows = table["grid"]?["rows"]?.Value<int>() ?? 0;
                        var cols = table["grid"]?["cols"]?.Value<int>() ?? 0;
                        var tokensAssigned = table["tokenCoverage"]?["tokenCountInCells"]?.Value<int>() ?? 0;
                        AddLog($"Detected table via GRIDLINES (page {pageIndex}, {tableId})");
                        AddLog($"Rows detected: {rows}");
                        AddLog($"Columns detected: {cols}");
                        AddLog($"Tokens assigned: {tokensAssigned}");
                    }
                }
            }

            var promotedCount = (root["recognition"]?["fields"] as JArray)?.Count ?? 0;
            AddLog($"Promoted recognition fields: {promotedCount}");
            var uniqueWordCount = (root["documentWords"] as JArray)?.Count ?? 0;
            AddLog($"Unique words (document): {uniqueWordCount}");
            AddLog($"Detected checkboxes: {totalCheckboxes}");
            AddLog($"Detected radios: {totalRadios}");
            AddLog($"Checked regions: {totalChecked}");
        }
        catch
        {
            AddLog("Unable to parse page timings from JSON.");
        }
    }

    private void LogDebugArtifacts(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var root = JObject.Parse(json);
            var artifacts = root["extensions"]?["debugArtifactPaths"] as JArray;
            if (artifacts is null || artifacts.Count == 0)
            {
                return;
            }

            foreach (var artifact in artifacts.OfType<JObject>())
            {
                var paths = new[]
                {
                    artifact["originalOrRenderPath"]?.Value<string>(),
                    artifact["grayPath"]?.Value<string>(),
                    artifact["preprocessedPath"]?.Value<string>(),
                    artifact["tokenOverlayPath"]?.Value<string>(),
                    artifact["lineOverlayPath"]?.Value<string>(),
                    artifact["blockOverlayPath"]?.Value<string>(),
                    artifact["tableOverlayPath"]?.Value<string>(),
                    artifact["regionOverlayPath"]?.Value<string>()
                };

                var pageIndex = artifact["pageIndex"]?.Value<int>() ?? 0;
                foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    AddLog($"Artifact (page {pageIndex}): {path}");
                }
            }
        }
        catch
        {
            AddLog("Unable to parse debug artifact paths from JSON.");
        }
    }

    private void LogNoiseDiagnostics(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var root = JObject.Parse(json);
            var diagnostics = root["extensions"]?["pageNoiseDiagnostics"] as JArray;
            if (diagnostics is null)
            {
                return;
            }

            foreach (var diag in diagnostics.OfType<JObject>())
            {
                var pageIndex = diag["pageIndex"]?.Value<int>() ?? 0;
                var totalTokenCount = diag["totalTokenCount"]?.Value<int>() ?? 0;
                var lowConfidenceTokenCount = diag["lowConfidenceTokenCount"]?.Value<int>() ?? 0;
                var suspectedNoiseCount = diag["suspectedDecorativeNoiseCount"]?.Value<int>() ?? 0;
                AddLog($"Noise (page {pageIndex}): tokens={totalTokenCount}, lowConf={lowConfidenceTokenCount}, suspectedNoise={suspectedNoiseCount}");
            }
        }
        catch
        {
            AddLog("Unable to parse page noise diagnostics from JSON.");
        }
    }

    private void PopulateTables(string json)
    {
        TableSummaries.Clear();
        SelectedTableHeaderColumns.Clear();
        SelectedTableHeaderCells.Clear();
        SelectedTableRawCells.Clear();
        SelectedTableDisplayRows.Clear();
        _tableJsonByKey.Clear();
        _tableOverlayPathByPage.Clear();
        TablesStatusMessage = "No tables detected for this run.";
        SelectedTableSummary = null;
        ClearSelectedTableDetails();

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var root = JObject.Parse(json);
            var artifactEntries = root["extensions"]?["debugArtifactPaths"] as JArray;
            if (artifactEntries is not null)
            {
                foreach (var artifact in artifactEntries.OfType<JObject>())
                {
                    var pageIndex = artifact["pageIndex"]?.Value<int>() ?? 0;
                    var overlayPath = artifact["tableOverlayPath"]?.Value<string>();
                    if (pageIndex > 0 && !string.IsNullOrWhiteSpace(overlayPath))
                    {
                        _tableOverlayPathByPage[pageIndex] = overlayPath;
                    }
                }
            }

            var pages = root["pages"] as JArray;
            if (pages is null || pages.Count == 0)
            {
                return;
            }

            var totalTables = 0;
            foreach (var page in pages.OfType<JObject>())
            {
                var pageIndex = page["pageIndex"]?.Value<int>() ?? 0;
                var tables = page["tables"] as JArray;
                if (tables is null || tables.Count == 0)
                {
                    continue;
                }

                AddLog($"Tables tab data: page {pageIndex} has {tables.Count} detected table(s).");
                totalTables += tables.Count;
                foreach (var table in tables.OfType<JObject>())
                {
                    var tableId = table["tableId"]?.Value<string>() ?? $"tbl-{totalTables:0000}";
                    var method = table["detection"]?["method"]?.Value<string>() ?? "unknown";
                    var confidence = table["confidence"]?.Value<double>() ?? 0;
                    var rowCount = table["grid"]?["rows"]?.Value<int>() ?? 0;
                    var columnCount = table["grid"]?["cols"]?.Value<int>() ?? 0;

                    var item = new TableSummaryItem
                    {
                        PageIndex = pageIndex,
                        TableId = tableId,
                        Method = method,
                        Confidence = confidence,
                        RowCount = rowCount,
                        ColumnCount = columnCount
                    };

                    TableSummaries.Add(item);
                    _tableJsonByKey[$"{pageIndex}:{tableId}"] = table;
                }
            }

            AddLog($"Detected table count total: {totalTables}");
            if (TableSummaries.Count > 0)
            {
                TablesStatusMessage = string.Empty;
                SelectedTableSummary = TableSummaries[0];
            }
        }
        catch
        {
            TablesStatusMessage = "Unable to parse table structures from OCR JSON.";
        }
    }

    private void PopulateWords(string json)
    {
        DisplayWords.Clear();
        WordScopeOptions.Clear();
        WordScopeOptions.Add("All");
        _pageWordsByPageIndex.Clear();
        _documentWords.Clear();
        DisplayWordCount = 0;
        SelectedWordScope = "All";

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var root = JObject.Parse(json);
            var documentWords = (root["documentWords"] as JArray)?
                .OfType<JValue>()
                .Select(v => v.Value<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            _documentWords.AddRange(documentWords);

            var pages = root["pages"] as JArray;
            if (pages is not null)
            {
                foreach (var page in pages.OfType<JObject>().OrderBy(p => p["pageIndex"]?.Value<int>() ?? int.MaxValue))
                {
                    var pageIndex = page["pageIndex"]?.Value<int>() ?? 0;
                    if (pageIndex <= 0)
                    {
                        continue;
                    }

                    var pageWords = (page["pageWords"] as JArray)?
                        .OfType<JValue>()
                        .Select(v => v.Value<string>())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!)
                        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                        .ToList() ?? [];

                    _pageWordsByPageIndex[pageIndex] = pageWords;
                    WordScopeOptions.Add($"Page {pageIndex}");
                }
            }

            foreach (var word in _documentWords)
            {
                DisplayWords.Add(word);
            }

            DisplayWordCount = DisplayWords.Count;
            AddLog($"Unique words (document): {_documentWords.Count}");
        }
        catch
        {
            DisplayWords.Clear();
            DisplayWordCount = 0;
            AddLog("Unable to parse word lists from JSON.");
        }
    }

    private void UpdateDisplayWordsForScope()
    {
        DisplayWords.Clear();
        IEnumerable<string> words = [];
        var scope = SelectedWordScope ?? "All";

        if (string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase))
        {
            words = _documentWords;
        }
        else if (scope.StartsWith("Page ", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(scope["Page ".Length..], out var pageIndex) &&
                 _pageWordsByPageIndex.TryGetValue(pageIndex, out var pageWords))
        {
            words = pageWords;
        }

        foreach (var word in words.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            DisplayWords.Add(word);
        }

        DisplayWordCount = DisplayWords.Count;
    }

    private void LoadSelectedTableDetails()
    {
        SelectedTableHeaderColumns.Clear();
        SelectedTableHeaderCells.Clear();
        SelectedTableRawCells.Clear();
        SelectedTableDisplayRows.Clear();

        if (SelectedTableSummary is null)
        {
            ClearSelectedTableDetails();
            TablesStatusMessage = TableSummaries.Count == 0
                ? "No tables detected for this run."
                : "Select a table to inspect details.";
            return;
        }

        var key = $"{SelectedTableSummary.PageIndex}:{SelectedTableSummary.TableId}";
        if (!_tableJsonByKey.TryGetValue(key, out var table))
        {
            ClearSelectedTableDetails();
            TablesStatusMessage = "Unable to load selected table details.";
            return;
        }

        SelectedTableId = SelectedTableSummary.TableId;
        SelectedTablePageIndex = SelectedTableSummary.PageIndex;
        SelectedTableMethod = table["detection"]?["method"]?.Value<string>() ?? "unknown";
        SelectedTableHasExplicitGridLines = table["detection"]?["hasExplicitGridLines"]?.Value<bool>() ?? false;
        SelectedTableConfidence = table["confidence"]?.Value<double>() ?? 0;
        SelectedTableTokenCoverageRatio = table["tokenCoverage"]?["coverageRatio"]?.Value<double>() ?? 0;
        SelectedTableBboxText = FormatBbox(table["bbox"] as JObject);
        SelectedTableOverlayPath = _tableOverlayPathByPage.GetValueOrDefault(SelectedTablePageIndex, string.Empty);
        if (!string.IsNullOrWhiteSpace(SelectedTableOverlayPath))
        {
            AddLog($"Selected table overlay path: {SelectedTableOverlayPath}");
        }

        var headerColumns = table["header"]?["columns"] as JArray;
        if (headerColumns is not null)
        {
            foreach (var column in headerColumns.OfType<JObject>())
            {
                SelectedTableHeaderColumns.Add(new TableHeaderColumnItem
                {
                    ColIndex = column["colIndex"]?.Value<int>() ?? 0,
                    Name = column["name"]?.Value<string>() ?? string.Empty,
                    Key = column["key"]?.Value<string>() ?? string.Empty,
                    Confidence = column["confidence"]?.Value<double>() ?? 0
                });
            }
        }

        var headerCells = table["header"]?["cells"] as JArray;
        if (headerCells is not null)
        {
            foreach (var cell in headerCells.OfType<JObject>())
            {
                SelectedTableHeaderCells.Add(new TableCellDisplayItem
                {
                    RowIndex = cell["rowIndex"]?.Value<int>() ?? 0,
                    ColIndex = cell["colIndex"]?.Value<int>() ?? 0,
                    Text = cell["text"]?.Value<string>() ?? string.Empty,
                    Confidence = cell["confidence"]?.Value<double>() ?? 0,
                    TokenCount = (cell["tokenIds"] as JArray)?.Count ?? 0
                });
            }
        }

        var rawCells = table["cells"] as JArray;
        if (rawCells is not null)
        {
            foreach (var cell in rawCells.OfType<JObject>())
            {
                SelectedTableRawCells.Add(new TableCellDisplayItem
                {
                    RowIndex = cell["rowIndex"]?.Value<int>() ?? 0,
                    ColIndex = cell["colIndex"]?.Value<int>() ?? 0,
                    Text = cell["text"]?.Value<string>() ?? string.Empty,
                    Confidence = cell["confidence"]?.Value<double>() ?? 0,
                    TokenCount = (cell["tokenIds"] as JArray)?.Count ?? 0
                });
            }
        }

        BuildDisplayRows(table);
        TablesStatusMessage = string.Empty;
    }

    private void BuildDisplayRows(JObject table)
    {
        var rows = table["rows"] as JArray;
        var columns = SelectedTableHeaderColumns
            .OrderBy(c => c.ColIndex)
            .Select(c => !string.IsNullOrWhiteSpace(c.Key) ? c.Key : c.Name)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        if (rows is not null && rows.Count > 0)
        {
            foreach (var row in rows.OfType<JObject>())
            {
                dynamic expando = new ExpandoObject();
                var dict = (IDictionary<string, object?>)expando;
                var rowIndex = row["rowIndex"]?.Value<int>() ?? 0;
                dict["row_index"] = rowIndex;
                var values = row["values"] as JObject;
                foreach (var key in columns)
                {
                    dict[key] = values?[key]?.ToString() ?? string.Empty;
                }

                SelectedTableDisplayRows.Add((ExpandoObject)expando);
            }

            return;
        }

        var groupedCells = SelectedTableRawCells
            .GroupBy(c => c.RowIndex)
            .OrderBy(g => g.Key)
            .ToList();
        if (groupedCells.Count == 0)
        {
            return;
        }

        foreach (var row in groupedCells)
        {
            dynamic expando = new ExpandoObject();
            var dict = (IDictionary<string, object?>)expando;
            dict["row_index"] = row.Key;
            foreach (var header in SelectedTableHeaderColumns.OrderBy(h => h.ColIndex))
            {
                var key = !string.IsNullOrWhiteSpace(header.Key) ? header.Key : $"col_{header.ColIndex}";
                var cell = row.FirstOrDefault(c => c.ColIndex == header.ColIndex);
                dict[key] = cell?.Text ?? string.Empty;
            }

            SelectedTableDisplayRows.Add((ExpandoObject)expando);
        }
    }

    private void ClearSelectedTableDetails()
    {
        SelectedTableId = string.Empty;
        SelectedTablePageIndex = 0;
        SelectedTableMethod = string.Empty;
        SelectedTableHasExplicitGridLines = false;
        SelectedTableConfidence = 0;
        SelectedTableBboxText = string.Empty;
        SelectedTableTokenCoverageRatio = 0;
        SelectedTableOverlayPath = string.Empty;
    }

    private static string FormatBbox(JObject? bbox)
    {
        if (bbox is null)
        {
            return string.Empty;
        }

        var x = bbox["x"]?.Value<int>() ?? 0;
        var y = bbox["y"]?.Value<int>() ?? 0;
        var w = bbox["w"]?.Value<int>() ?? 0;
        var h = bbox["h"]?.Value<int>() ?? 0;
        return $"x={x}, y={y}, w={w}, h={h}";
    }

    private void PopulatePreviewArtifacts(string json)
    {
        _previewArtifactsByPage.Clear();
        PreviewPages.Clear();
        PreviewImage = null;
        PreviewStatusMessage = "No preview artifacts available for this run.";

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var root = JObject.Parse(json);
            var artifacts = root["extensions"]?["debugArtifactPaths"] as JArray;
            if (artifacts is null || artifacts.Count == 0)
            {
                PreviewStatusMessage = "Enable Save Debug Artifacts and Save Token Overlay to generate preview assets.";
                return;
            }

            foreach (var artifact in artifacts.OfType<JObject>())
            {
                var pageIndex = artifact["pageIndex"]?.Value<int>() ?? 0;
                if (pageIndex <= 0)
                {
                    continue;
                }

                _previewArtifactsByPage[pageIndex] = new PageArtifactSet
                {
                    OriginalOrRenderPath = artifact["originalOrRenderPath"]?.Value<string>(),
                    GrayPath = artifact["grayPath"]?.Value<string>(),
                    PreprocessedPath = artifact["preprocessedPath"]?.Value<string>(),
                    TokenOverlayPath = artifact["tokenOverlayPath"]?.Value<string>(),
                    LineOverlayPath = artifact["lineOverlayPath"]?.Value<string>(),
                    BlockOverlayPath = artifact["blockOverlayPath"]?.Value<string>(),
                    TableOverlayPath = artifact["tableOverlayPath"]?.Value<string>(),
                    RegionOverlayPath = artifact["regionOverlayPath"]?.Value<string>()
                };
            }

            foreach (var page in _previewArtifactsByPage.Keys.OrderBy(p => p))
            {
                PreviewPages.Add(page);
            }

            if (PreviewPages.Count > 0)
            {
                SelectedPreviewPage = PreviewPages[0];
                UpdatePreviewImage();
            }
        }
        catch
        {
            PreviewStatusMessage = "Unable to load preview artifacts from OCR JSON.";
        }
    }

    private void UpdatePreviewImage()
    {
        PreviewImage = null;

        if (!_previewArtifactsByPage.TryGetValue(SelectedPreviewPage, out var artifacts))
        {
            PreviewStatusMessage = "No debug artifacts available for this page.";
            return;
        }

        var path = SelectedPreviewView switch
        {
            "Original/Rendered" => artifacts.OriginalOrRenderPath,
            "Grayscale" => artifacts.GrayPath,
            "Preprocessed" => artifacts.PreprocessedPath,
            "Overlay" => artifacts.TokenOverlayPath,
            "Lines Overlay" => artifacts.LineOverlayPath,
            "Blocks Overlay" => artifacts.BlockOverlayPath,
            "Table Overlay" => artifacts.TableOverlayPath,
            "Region Overlay" => artifacts.RegionOverlayPath,
            _ => artifacts.PreprocessedPath
        };

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewStatusMessage = SelectedPreviewView.Contains("Overlay", StringComparison.OrdinalIgnoreCase)
                ? "No overlay artifact available for this page."
                : "Enable Save Debug Artifacts and Save Token Overlay to generate preview assets.";
            return;
        }

        PreviewImage = LoadBitmap(path);
        PreviewStatusMessage = string.Empty;
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ZoomIn()
    {
        PreviewZoom = Math.Min(8.0, PreviewZoom + 0.1);
    }

    private void ZoomOut()
    {
        PreviewZoom = Math.Max(0.1, PreviewZoom - 0.1);
    }

    private void FitToWindow()
    {
        if (PreviewImage is null || _previewViewportWidth <= 0 || _previewViewportHeight <= 0)
        {
            PreviewZoom = 1.0;
            return;
        }

        var scaleX = _previewViewportWidth / PreviewImage.PixelWidth;
        var scaleY = _previewViewportHeight / PreviewImage.PixelHeight;
        PreviewZoom = Math.Max(0.1, Math.Min(scaleX, scaleY));
    }

    public void SetPreviewViewportSize(double width, double height)
    {
        _previewViewportWidth = width;
        _previewViewportHeight = height;
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogLines.Add($"[{timestamp}] {message}");
    }

    private sealed class PageArtifactSet
    {
        public string? OriginalOrRenderPath { get; init; }
        public string? GrayPath { get; init; }
        public string? PreprocessedPath { get; init; }
        public string? TokenOverlayPath { get; init; }
        public string? LineOverlayPath { get; init; }
        public string? BlockOverlayPath { get; init; }
        public string? TableOverlayPath { get; init; }
        public string? RegionOverlayPath { get; init; }
    }
}
