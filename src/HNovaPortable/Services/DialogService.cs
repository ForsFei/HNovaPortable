using System.Windows;

namespace HNovaPortable.Services;

/// <summary>统一对话框入口，避免各视图直接 new Window 造成耦合。</summary>
public sealed class DialogService
{
    public bool? ShowDialog(Window window)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is not null && !ReferenceEquals(owner, window) && owner.IsVisible)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return window.ShowDialog();
    }

    public void Info(string message, string title = "HNova Portable")
        => ShowMessage(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warn(string message, string title = "HNova Portable")
        => ShowMessage(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Error(string message, string title = "HNova Portable")
        => ShowMessage(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title = "请确认", bool danger = false)
        => ShowMessage(message, title, MessageBoxButton.OKCancel,
            danger ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.OK;

    private static MessageBoxResult ShowMessage(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Application.Current?.MainWindow;
        return owner is not null && owner.IsVisible
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }
}
