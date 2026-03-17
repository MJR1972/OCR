using System.Windows;
using OcrShowcase.Demo.Wpf.ViewModels;

namespace OcrShowcase.Demo.Wpf;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdatePreviewViewportSize();
    }

    private void PreviewScrollHost_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewViewportSize();
    }

    private void UpdatePreviewViewportSize()
    {
        if (PreviewViewport is null)
        {
            return;
        }

        var width = Math.Max(1, PreviewViewport.ActualWidth - 56);
        var height = Math.Max(1, PreviewViewport.ActualHeight - 56);
        _viewModel.UpdatePreviewViewportSize(width, height);
    }
}
