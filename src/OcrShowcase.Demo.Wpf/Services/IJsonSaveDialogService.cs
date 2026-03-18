namespace OcrShowcase.Demo.Wpf.Services;

public interface IJsonSaveDialogService
{
    string? PickSavePath(string suggestedFileName, string? initialDirectory);
}
