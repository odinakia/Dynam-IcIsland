using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.UI;

public static class FileVisuals
{
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico",
    };

    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static ImageSource? GetThumbnail(string path)
    {
        try
        {
            if (IsImage(path) && File.Exists(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = 64;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        catch
        {
            // битые картинки просто получат иконку
        }

        return GetIcon(path);
    }

    public static ImageSource? GetIcon(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = "file";

        if (IconCache.TryGetValue(ext, out var cached)) return cached;

        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();

            if (IconCache.Count > 48) IconCache.Clear();
            IconCache[ext] = source;
            return source;
        }
        catch
        {
            return null;
        }
    }
}
