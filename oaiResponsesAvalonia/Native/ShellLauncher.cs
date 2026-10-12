using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace oaiResponsesAvalonia.Native
{
    /// <summary>
    /// Provides platform-specific behavior for selecting an application to open a specified file.
    /// </summary>
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
        /// Opens the platform's native Open With experience for a file.
        /// </summary>
        /// <param name="parentWindow">The Avalonia window that owns the Windows dialog.</param>
        /// <param name="filePath">The path of the file to offer to external applications.</param>
        public static void OpenWith(Window parentWindow, string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            if (OperatingSystem.IsWindows())
            {
                ShowWindowsOpenWithDialog(parentWindow, filePath);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                StartProcess("open", "-R", filePath);
                return;
            }

            throw new PlatformNotSupportedException("Open With is not supported on this platform.");
        }

        private static void ShowWindowsOpenWithDialog(Window parentWindow, string filePath)
        {
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

        private static void StartProcess(string fileName, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
        }
    }
}
