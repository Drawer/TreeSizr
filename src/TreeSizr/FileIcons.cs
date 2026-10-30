using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TreeSizr;

/// <summary>
/// Small shell icons for files, as Explorer shows them. Icons are cached per extension; only file types
/// that carry their own icon (programs, shortcuts, icon files) are looked up per file.
/// Must be used on the UI thread.
/// </summary>
public static class FileIcons
{
    private static readonly HashSet<string> OwnIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".ico", ".lnk", ".url", ".cur", ".ani", ".scr", ".appref-ms",
    };

    /// <summary>Opening a cloud-only file to read its icon would download it from OneDrive.</summary>
    private const FileAttributes CloudOnlyAttributes =
        FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    private static readonly Dictionary<string, ImageSource?> ByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> ByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The standard folder icon.</summary>
    public static ImageSource? Folder { get; } = Load("folder", useAttributesOnly: true, FileAttributeDirectory);

    /// <summary>A plain document icon, for the "[N Files]" group.</summary>
    public static ImageSource? GenericFile { get; } = Load("file", useAttributesOnly: true);

    public static ImageSource? Get(string path)
    {
        string extension = Path.GetExtension(path);
        if (OwnIconExtensions.Contains(extension) && !IsCloudOnly(path))
        {
            if (!ByPath.TryGetValue(path, out var own))
                ByPath[path] = own = Load(path, useAttributesOnly: false);
            if (own is not null) return own;
        }

        if (!ByExtension.TryGetValue(extension, out var icon))
            ByExtension[extension] = icon = Load("file" + extension, useAttributesOnly: true);
        return icon;
    }

    private static bool IsCloudOnly(string path)
    {
        try { return (File.GetAttributes(path) & CloudOnlyAttributes) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static BitmapSource? Load(string path, bool useAttributesOnly, uint attributes = FileAttributeNormal)
    {
        uint flags = ShgfiIcon | ShgfiSmallIcon | (useAttributesOnly ? ShgfiUseFileAttributes : 0);
        var info = new ShFileInfo();
        if (SHGetFileInfoW(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint ShgfiIcon = 0x100, ShgfiSmallIcon = 0x1, ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeNormal = 0x80, FileAttributeDirectory = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint fileAttributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>File path → its shell icon.</summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string path ? FileIcons.Get(path) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
