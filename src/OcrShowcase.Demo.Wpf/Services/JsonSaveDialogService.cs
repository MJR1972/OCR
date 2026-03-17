using Microsoft.Win32;

namespace OcrShowcase.Demo.Wpf.Services;

public sealed class JsonSaveDialogService : IJsonSaveDialogService
{
    private const string JsonFilter = "JSON files|*.json|All files|*.*";

    public string? PickSavePath(string suggestedFileName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = JsonFilter,
            FileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "ocr-output.json" : suggestedFileName,
            InitialDirectory = string.IsNullOrWhiteSpace(initialDirectory) ? null : initialDirectory,
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
