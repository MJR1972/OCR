using System.Windows;
using Ocr.TestHarness.Wpf.ViewModels;

namespace Ocr.TestHarness.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    private void PreviewScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.SetPreviewViewportSize(e.NewSize.Width, e.NewSize.Height);
    }
}
