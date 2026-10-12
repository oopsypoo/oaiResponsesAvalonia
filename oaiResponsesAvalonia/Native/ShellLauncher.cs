using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace oaiResponsesAvalonia.Native
{
    /// <summary>
    /// Provides functionality to display the Windows "Open With" dialog for selecting an application to open a
    /// specified file.
    /// </summary>
    /// <remarks>This class enables integration with the standard Windows "Open With" dialog, allowing users
    /// to choose an application to open a file at runtime. It is intended for use in desktop applications that require
    /// user-driven file association selection.</remarks>
    class ShellLauncher
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO poainfo);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENASINFO
        {
            public string pcszFile;
            public string pcszClass;
            public uint oaInFlags;
        }

        // Flags to tell Windows to actually execute the app once selected
        private const uint OAIF_ALLOW_REGISTRATION = 0x01;
        private const uint OAIF_EXEC = 0x04;

        /// <summary>
        /// Displays the Windows "Open with" dialog for a file, owned by the specified Avalonia window.
        /// </summary>
        /// <remarks>
        /// This uses the Windows Shell API and is unavailable on other platforms. The regular "Open" action
        /// uses the platform's default file association instead.
        /// </remarks>
        /// <param name="parentWindow">The Avalonia window that owns the native dialog.</param>
        /// <param name="filePath">The path of the file to offer to external applications.</param>
        public static void ShowOpenWithDialog(Window parentWindow, string filePath)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("The Open With dialog is only available on Windows.");

            IntPtr hwnd = parentWindow.TryGetPlatformHandle()?.Handle
                ?? throw new InvalidOperationException("The parent window does not have a native platform handle.");

            var info = new OPENASINFO
            {
                pcszFile = filePath,
                pcszClass = null,
                oaInFlags = OAIF_ALLOW_REGISTRATION | OAIF_EXEC
            };

            SHOpenWithDialog(hwnd, ref info);
        }
    }
}
