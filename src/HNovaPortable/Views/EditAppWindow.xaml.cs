using System.Windows;
using HNovaPortable.ViewModels;

namespace HNovaPortable.Views;

public partial class EditAppWindow : Window
{
    public EditAppWindow(AppEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, bool saved)
    {
        DialogResult = saved;
        Close();
    }
}
