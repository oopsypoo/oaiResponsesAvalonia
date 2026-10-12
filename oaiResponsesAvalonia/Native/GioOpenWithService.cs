using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace oaiResponsesAvalonia.Native
{
    /// <summary>
    /// Represents an application entry with its ID, name, icon, and default status.
    /// Used in linux systems to represent applications that can open files of a specific type.
    /// </summary>
    /// <param name="Id">The ID of the application.</param>
    /// <param name="Name">The display name of the application.</param>
    /// <param name="Icon">The icon of the application.</param>
    /// <param name="IsDefault">Indicates whether the application is the default for its type.</param>
    internal sealed record AppEntry(string Id, string Name, string? Icon, bool IsDefault);
    internal static class GioOpenWithService
    {
        private const string Gio = "libgio-2.0.so.0";
        private const string GObject = "libgobject-2.0.so.0";
        private const string GLib = "libglib-2.0.so.0";

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_file_new_for_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_file_query_info(
            IntPtr file,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string attributes,
            int flags,
            IntPtr cancellable,
            ref IntPtr error);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_file_info_get_content_type(IntPtr info);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_app_info_get_all_for_type([MarshalAs(UnmanagedType.LPUTF8Str)] string contentType);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_app_info_get_default_for_type(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType,
            int mustSupportUris);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_app_info_get_id(IntPtr appInfo);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_app_info_get_display_name(IntPtr appInfo);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g_app_info_should_show(IntPtr appInfo);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g_app_info_launch(IntPtr appInfo, IntPtr files, IntPtr context, ref IntPtr error);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_desktop_app_info_new([MarshalAs(UnmanagedType.LPUTF8Str)] string desktopId);

        [DllImport(GObject, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_object_unref(IntPtr obj);

        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_list_append(IntPtr list, IntPtr data);

        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_list_free(IntPtr list);

        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_error_free(IntPtr error);
        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_app_info_get_all();

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_app_info_get_icon(IntPtr appInfo);

        [DllImport(Gio, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr g_icon_to_string(IntPtr icon);

        [DllImport(GLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g_free(IntPtr mem);

        private static string? GetIconString(IntPtr app)
        {
            var icon = g_app_info_get_icon(app);
            if (icon == IntPtr.Zero) return null;
            var s = g_icon_to_string(icon);
            if (s == IntPtr.Zero) return null;
            try
            {
                var str = Marshal.PtrToStringUTF8(s);
                // Themed icons with several names serialize as ". GThemedIcon name1 name2"
                if (str is not null && str.StartsWith(". GThemedIcon "))
                    str = str.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(2).FirstOrDefault();
                return str;
            }
            finally { g_free(s); }
        }
        private static List<AppEntry> ReadList(IntPtr list, string? defaultId = null)
        {
            var result = new List<AppEntry>();
            try
            {
                for (IntPtr node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                {
                    IntPtr app = Marshal.ReadIntPtr(node);
                    if (app == IntPtr.Zero) continue;
                    try
                    {
                        if (g_app_info_should_show(app) == 0) continue;
                        string? id = Marshal.PtrToStringUTF8(g_app_info_get_id(app));
                        string? name = Marshal.PtrToStringUTF8(g_app_info_get_display_name(app));
                        if (id is not null && name is not null)
                            result.Add(new AppEntry(id, name, GetIconString(app),
                                string.Equals(id, defaultId, StringComparison.Ordinal)));
                    }
                    finally { g_object_unref(app); }
                }
            }
            finally { g_list_free(list); }
            return result;
        }

        internal static List<AppEntry> GetAllApps()
        {
            EnsureLinux();
            return ReadList(g_app_info_get_all())
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        internal static List<AppEntry> GetApps(string path)
        {
            EnsureLinux();
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var result = new List<AppEntry>();
            IntPtr file = g_file_new_for_path(path);
            if (file == IntPtr.Zero)
                return result;

            try
            {
                IntPtr error = IntPtr.Zero;
                IntPtr info = g_file_query_info(file, "standard::content-type", 0, IntPtr.Zero, ref error);
                FreeError(error);
                if (info == IntPtr.Zero)
                    return result;

                string? contentType;
                try
                {
                    contentType = Marshal.PtrToStringUTF8(g_file_info_get_content_type(info));
                }
                finally
                {
                    g_object_unref(info);
                }

                if (string.IsNullOrWhiteSpace(contentType))
                    return result;

                string? defaultId = null;
                IntPtr defaultApp = g_app_info_get_default_for_type(contentType, 0);
                if (defaultApp != IntPtr.Zero)
                {
                    try
                    {
                        defaultId = Marshal.PtrToStringUTF8(g_app_info_get_id(defaultApp));
                    }
                    finally
                    {
                        g_object_unref(defaultApp);
                    }
                }

                result.AddRange(ReadList(g_app_info_get_all_for_type(contentType), defaultId));
            }
            finally
            {
                g_object_unref(file);
            }

            return result.OrderByDescending(app => app.IsDefault).ToList();
        }
        // GError { guint32 domain; gint code; gchar* message; } -> message is at offset 8
        private static string? TakeErrorMessage(IntPtr error)
        {
            if (error == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 2 * sizeof(int))); }
            finally { g_error_free(error); }
        }
        internal static bool Launch(string appId, string path, out string? errorMessage)
        {
            EnsureLinux();
            ArgumentException.ThrowIfNullOrWhiteSpace(appId);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            errorMessage = null;

            IntPtr app = g_desktop_app_info_new(appId);
            if (app == IntPtr.Zero)
                return false;

            try
            {
                IntPtr file = g_file_new_for_path(path);
                if (file == IntPtr.Zero)
                    return false;

                try
                {
                    IntPtr files = g_list_append(IntPtr.Zero, file);
                    try
                    {
                        IntPtr error = IntPtr.Zero;
                        int launched = g_app_info_launch(app, files, IntPtr.Zero, ref error);
                        errorMessage = TakeErrorMessage(error);
                        return launched != 0;
                    }
                    finally
                    {
                        g_list_free(files);
                    }
                }
                finally
                {
                    g_object_unref(file);
                }
            }
            finally
            {
                g_object_unref(app);
            }
        }

        private static void EnsureLinux()
        {
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException("GIO Open With is available only on Linux.");
        }

        private static void FreeError(IntPtr error)
        {
            if (error != IntPtr.Zero)
                g_error_free(error);
        }
    }
}
