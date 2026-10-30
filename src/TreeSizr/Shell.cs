using System.Diagnostics;
using System.Windows;

namespace TreeSizr;

internal static class Shell
{
    public static void OpenFolder(string path) =>
        Start("explorer.exe", $"\"{path}\"");

    /// <summary>Opens Explorer with the given file or folder selected.</summary>
    public static void SelectInExplorer(string path) =>
        Start("explorer.exe", $"/select,\"{path}\"");

    public static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard is briefly locked by another process; one retry is usually enough.
            Thread.Sleep(50);
            try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    private static void Start(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TreeSizr", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
