using System.IO;
using System.Windows.Media;

namespace DynamicIsland.UI;

public sealed class ShelfItem
{
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ShelfItem(string path)
    {
        Path = path;
        Name = GetName(path);
    }

    public string Path { get; }
    public string Name { get; }

    /// <summary>Превью грузится лениво — только когда полка реально показана.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnail = FileVisuals.GetThumbnail(Path);
                _thumbnailLoaded = true;
            }

            return _thumbnail;
        }
    }

    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    private static string GetName(string path)
    {
        try
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }
}
