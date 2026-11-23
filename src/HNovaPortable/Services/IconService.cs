using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using HNovaPortable.Core;

namespace HNovaPortable.Services;

/// <summary>
/// 图标提取与缓存。图标统一保存为 Data/icons/&lt;appId&gt;.png（库内相对资源），
/// 这样即使软件被删除、迁到新电脑，图标依然跟随库存在。
/// </summary>
public sealed class IconService(AppPaths paths)
{
    private static readonly int[] PreferredSizes = [256, 128, 64, 48, 32, 24, 16];

    private readonly AppPaths _paths = paths;

    /// <summary>提取并保存图标，返回 icon_key（文件名）。失败返回 null。</summary>
    public Task<string?> CaptureAsync(string executablePath, string appId, CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => Capture(executablePath, appId), System.Windows.Threading.DispatcherPriority.Background, cancellationToken).Task;
        }

        return Task.FromResult(Capture(executablePath, appId));
    }

    public string? Capture(string executablePath, string appId)
    {
        var bitmap = ExtractBitmap(executablePath);
        if (bitmap is null)
        {
            return null;
        }

        try
        {
            var fileName = appId + ".png";
            var target = Path.Combine(_paths.IconsDirectory, fileName);
            Directory.CreateDirectory(_paths.IconsDirectory);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            return fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>从库内缓存加载图标；没有缓存时尝试实时从 EXE 提取。</summary>
    public BitmapSource? Load(string? iconKey, string? executablePath = null)
    {
        if (!string.IsNullOrWhiteSpace(iconKey))
        {
            var path = Path.Combine(_paths.IconsDirectory, Path.GetFileName(iconKey));
            if (File.Exists(path))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    image.UriSource = new Uri(path);
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
                {
                    // 落到实时提取
                }
            }
        }

        return string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)
            ? null
            : ExtractBitmap(executablePath);
    }

    private static BitmapSource? ExtractBitmap(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        foreach (var size in PreferredSizes)
        {
            var handle = ExtractIcon(executablePath, size);
            if (handle == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                if (bitmap.PixelWidth >= 8)
                {
                    return bitmap;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 尝试下一个尺寸
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        return null;
    }

    private static IntPtr ExtractIcon(string path, int size)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            var count = PrivateExtractIcons(path, 0, size, size, handles, ids, 1, 0);
            return count > 0 && handles[0] != IntPtr.Zero ? handles[0] : IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
