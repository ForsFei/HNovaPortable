using System.Windows.Controls;
using System.Windows.Documents;
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

    internal void ShowUpdateResults(IReadOnlyList<(string Name, string? GithubUrl, AppUpdateResult Result)> results)
    {
        var window = new Window
        {
            Title = "全库更新结果",
            Width = 620,
            Height = 380,
            MinWidth = 420,
            MinHeight = 250,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.SetResourceReference(Window.BackgroundProperty, "BrushBgSurface");
        var document = new FlowDocument { PagePadding = new Thickness(12) };
        if (results.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("软件库为空")));
        }

        foreach (var (name, githubUrl, result) in results.OrderByDescending(row => row.Result.HasUpdate))
        {
            var status = new Run(result.Message);
            if (result.HasUpdate)
            {
                status.FontWeight = FontWeights.SemiBold;
                status.SetResourceReference(TextElement.ForegroundProperty, "BrushAccent");
            }

            var row = new Paragraph { Margin = new Thickness(0, 0, 0, 14) };
            row.Inlines.Add(new Bold(new Run(name)));
            row.Inlines.Add(new LineBreak());
            row.Inlines.Add(status);
            if (Uri.TryCreate(githubUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com"
                && string.IsNullOrEmpty(uri.UserInfo))
            {
                var link = new Hyperlink(new Run(uri.AbsoluteUri)) { NavigateUri = uri };
                link.SetResourceReference(TextElement.ForegroundProperty, "BrushAccent");
                link.RequestNavigate += (_, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
                        {
                            UseShellExecute = true,
                        });
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        Warn("无法打开浏览器，可复制仓库地址。");
                    }
                };
                row.Inlines.Add(new LineBreak());
                row.Inlines.Add(link);
            }

            document.Blocks.Add(row);
        }

        var text = new RichTextBox
        {
            Document = document,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Margin = new Thickness(18),
            Padding = new Thickness(10),
        };
        text.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "BrushBgSurfaceAlt");
        text.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "BrushTextPrimary");
        text.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "BrushBorderSubtle");
        window.Content = text;
        ShowDialog(window);
    }

    internal void ShowStartupManager(IReadOnlyList<Models.AppEntry> entries, Core.AppPaths paths)
    {
        var window = new Window { Title = "开机自启动管理", Width = 540, Height = 420, MinWidth = 380, MinHeight = 260 };
        window.SetResourceReference(Window.BackgroundProperty, "BrushBgSurface");
        var content = new DockPanel { Margin = new Thickness(20) };
        var hint = new TextBlock
        {
            Text = "勾选后会在当前用户登录 Windows 时启动。若软件已自行设置自启，请选用其中一种方式。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "BrushTextSecondary");
        DockPanel.SetDock(hint, Dock.Top);
        content.Children.Add(hint);
        var list = new StackPanel();
        foreach (var entry in entries)
        {
            var item = new CheckBox
            {
                Content = entry.Name,
                IsChecked = StartupService.IsEnabled(entry),
                Margin = new Thickness(0, 5, 0, 5),
            };
            item.SetResourceReference(Control.ForegroundProperty, "BrushTextPrimary");
            item.Click += (_, _) =>
            {
                try
                {
                    StartupService.SetEnabled(entry, paths, item.IsChecked == true);
                }
                catch (Exception ex)
                {
                    item.IsChecked = StartupService.IsEnabled(entry);
                    MessageBox.Show(window, ex.Message, "自启动设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            list.Children.Add(item);
        }

        content.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        window.Content = content;
        ShowDialog(window);
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
