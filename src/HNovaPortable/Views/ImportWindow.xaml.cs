using System.ComponentModel;
using System.Windows;
using HNovaPortable.ViewModels;

namespace HNovaPortable.Views;

public partial class ImportWindow : Window
{
    private readonly ImportWizardViewModel _viewModel;

    public ImportWindow(ImportWizardViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 导入进行中不允许直接关闭，先请求取消并等待清理。
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
