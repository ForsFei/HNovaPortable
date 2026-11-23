using System.ComponentModel;
using System.Windows;
using HNovaPortable.ViewModels;

namespace HNovaPortable.Views;

public partial class ExportWindow : Window
{
    private readonly ExportViewModel _viewModel;

    public ExportWindow(ExportViewModel viewModel)
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
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            _viewModel.CancelCommand.Execute(null);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
