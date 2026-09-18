using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DesktopQuickAccess;

/// <summary>
/// シェルAPIからファイル/フォルダに紐づくアイコンを取得する。
/// メニューを開くたびに毎回シェルへ問い合わせると体感速度が悪化するため、
/// 同じ見た目になるアイコン(拡張子単位)はキャッシュして使い回す。
/// </summary>
internal static class ShellIcon
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ショートカット/実行ファイルなど、ファイルごとにアイコンが異なりうる拡張子。
    // これら以外は同じ拡張子のファイル同士でアイコン画像を共有してよい。
    private static readonly HashSet<string> PerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".scr", ".cpl", ".msc",
    };

    private const string NoExtensionCacheKey = "\0noext";

    private static readonly ConcurrentDictionary<string, Image?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Image? GetIcon(string path, bool isDirectory)
    {
        var key = GetCacheKey(path, isDirectory);
        return Cache.GetOrAdd(key, _ => FetchIcon(path));
    }

    private static string GetCacheKey(string path, bool isDirectory)
    {
        // フォルダはdesktop.iniによるカスタムアイコンやオーバーレイ(ランサムウェア対策の
        // 目印フォルダ等)で見た目が異なる場合があるため、共有キャッシュにせず個別に取得する。
        if (isDirectory)
        {
            return path;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return NoExtensionCacheKey;
        }

        return PerFileExtensions.Contains(ext) ? path : ext;
    }

    private static Image? FetchIcon(string path)
    {
        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(info.hIcon);
            return (Image)icon.ToBitmap().Clone();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
