using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RiosvImports.Setup;

/// <summary>
/// Изборът на папка — прозорецът на Explorer, а не старото дърво.
///
/// FolderBrowserDialog в .NET Framework е дървото от Windows XP: без адресна
/// лента, без търсене, без „Бърз достъп“. Тук се вика IFileOpenDialog с
/// FOS_PICKFOLDERS — същият прозорец, с който се отваря файл. Ако не стане
/// (рядко: стари политики), остава дървото.
/// </summary>
internal static class FolderPicker
{
    public static string? Pick(IWin32Window owner, string title, string? start)
    {
        try
        {
            return Modern(owner, title, start);
        }
        catch (COMException)
        {
            return Classic(owner, title, start);
        }
        catch (InvalidCastException)
        {
            return Classic(owner, title, start);
        }
    }

    private static string? Modern(IWin32Window owner, string title, string? start)
    {
        var dialog = (IFileDialog)new FileOpenDialogCoClass();

        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist | FosNoChangeDir);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("Избери");

            if (Existing(start) is { } folder)
            {
                var iid = typeof(IShellItem).GUID;

                if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out var item) == 0 && item is not null)
                {
                    dialog.SetFolder(item);
                    Marshal.ReleaseComObject(item);
                }
            }

            var hr = dialog.Show(owner.Handle);

            if (hr == ErrorCancelled) return null;
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out var result);

            try
            {
                result.GetDisplayName(SigdnFileSysPath, out var path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(result);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static string? Classic(IWin32Window owner, string title, string? start)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = title,
            ShowNewFolderButton = true,
            SelectedPath = Existing(start) ?? string.Empty
        };

        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    /// <summary>Най-близката съществуваща папка нагоре — оттам започва изборът.</summary>
    private static string? Existing(string? path)
    {
        try
        {
            var folder = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path!.Trim());

            while (folder is not null && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);

            return folder;
        }
        catch
        {
            return null;
        }
    }

    private const uint FosNoChangeDir = 0x00000008;
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCoClass
    {
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
