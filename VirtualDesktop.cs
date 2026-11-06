using System;
using System.Runtime.InteropServices;

namespace TextDock;

/// <summary>
/// Pins a window to every virtual desktop. This is the same call Task View makes for
/// "Show this window on all desktops". There is no public API for it, so it goes through
/// the shell's IVirtualDesktopPinnedApps service. Those IIDs have been stable from
/// Windows 10 1809 through Windows 11 25H2; the IVirtualDesktopManagerInternal IIDs that
/// churn between builds aren't needed for pinning.
/// </summary>
internal static class VirtualDesktop
{
    private static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid CLSID_VirtualDesktopPinnedApps = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? result);
    }

    // Only the leading vtable slots we call are declared; order matters, the rest can be omitted.
    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IntPtr array);
        [PreserveSig] int GetViewsByZOrder(out IntPtr array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
    }

    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedApps
    {
        [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId, out int pinned);
        [PreserveSig] int PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int IsViewPinned(IntPtr view, out int pinned);
        [PreserveSig] int PinView(IntPtr view);
        [PreserveSig] int UnpinView(IntPtr view);
    }

    /// <summary>Pins or unpins the window. Returns false (with a reason) if the shell refused.</summary>
    public static bool TrySetPinned(IntPtr hwnd, bool pinned, out string error)
        => Run(hwnd, pinned, out _, out error);

    /// <summary>Reads the pin state without changing it. Null if it couldn't be determined.</summary>
    public static bool? IsPinned(IntPtr hwnd)
        => Run(hwnd, null, out var state, out _) ? state : null;

    private static bool Run(IntPtr hwnd, bool? target, out bool state, out string error)
    {
        state = false;
        error = "";
        object? shell = null, collectionObj = null, pinnedObj = null;
        IntPtr view = IntPtr.Zero;
        try
        {
            var shellType = Type.GetTypeFromCLSID(CLSID_ImmersiveShell, throwOnError: true)!;
            shell = Activator.CreateInstance(shellType);
            var provider = (IServiceProvider)shell!;

            var sid = typeof(IApplicationViewCollection).GUID;
            var iid = sid;
            int hr = provider.QueryService(ref sid, ref iid, out collectionObj);
            if (hr < 0 || collectionObj is null) { error = $"ApplicationViewCollection unavailable (0x{hr:X8})"; return false; }

            sid = CLSID_VirtualDesktopPinnedApps;
            iid = typeof(IVirtualDesktopPinnedApps).GUID;
            hr = provider.QueryService(ref sid, ref iid, out pinnedObj);
            if (hr < 0 || pinnedObj is null) { error = $"VirtualDesktopPinnedApps unavailable (0x{hr:X8})"; return false; }

            var collection = (IApplicationViewCollection)collectionObj;
            var pinnedApps = (IVirtualDesktopPinnedApps)pinnedObj;

            // The view only exists once the window has been shown with a taskbar presence.
            hr = collection.GetViewForHwnd(hwnd, out view);
            if (hr < 0 || view == IntPtr.Zero) { error = $"no application view for window (0x{hr:X8})"; return false; }

            hr = pinnedApps.IsViewPinned(view, out int current);
            if (hr < 0) { error = $"IsViewPinned failed (0x{hr:X8})"; return false; }
            state = current != 0;
            if (target is null || target == state) return true;

            hr = target.Value ? pinnedApps.PinView(view) : pinnedApps.UnpinView(view);
            if (hr < 0) { error = $"{(target.Value ? "PinView" : "UnpinView")} failed (0x{hr:X8})"; return false; }
            state = target.Value;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (view != IntPtr.Zero) Marshal.Release(view);
            Release(pinnedObj);
            Release(collectionObj);
            Release(shell);
        }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }
}
