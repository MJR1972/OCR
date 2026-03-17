using Microsoft.Win32;

namespace OcrShowcase.Demo.Wpf.Services;

public sealed class InputFileDialogService : IInputFileDialogService
{
    private const string SupportedFileFilter =
        "Supported files|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.gif;*.bmp|" +
        "PDF files|*.pdf|" +
        "Image files|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.gif;*.bmp|" +
        "All files|*.*";

    public string? PickInputFile()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = SupportedFileFilter
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
