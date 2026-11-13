using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Mia;

internal static class NativeDesktop
{
    private const int ExtendedStyle = -20;
    private const long TopmostStyle = 0x00000008;
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private static readonly IntPtr TopmostWindow = new(-1);

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device; }
    internal readonly record struct Surface(IntPtr Handle, Rect Bounds);
    private delegate bool MonitorProc(IntPtr monitor,IntPtr dc,ref Rect bounds,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorProc callback,IntPtr data);
    internal static List<Rect> WorkAreas()
    {
        var result=new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr monitor,IntPtr dc,ref Rect bounds,IntPtr data)=>
        {
            var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfo(monitor,ref info))result.Add(info.Work);return true;
        },IntPtr.Zero);return result;
    }
    internal static List<ScreenRegion> Screens()
    {
        var result=new List<ScreenRegion>();
        EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr monitor,IntPtr dc,ref Rect bounds,IntPtr data)=>
        {
            var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfo(monitor,ref info))result.Add(new ScreenRegion(info.Device,info.Work.Left,info.Work.Top,info.Work.Right,info.Work.Bottom,(info.Flags&1)!=0,info.Monitor.Width,info.Monitor.Height));
            return true;
        },IntPtr.Zero);
        result.Sort((a,b)=>StringComparer.OrdinalIgnoreCase.Compare(a.Id,b.Id));return result;
    }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out Rect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attr, out int value, int size);

    internal static Rect WorkArea(Point point)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(point, 2), ref info);
        return info.Work;
    }
    internal static void Position(IntPtr hwnd, double x, double y) =>
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, 0x0001 | 0x0004 | 0x0010);

    internal static void RestorePetTopmost(IntPtr hwnd, Rect petBounds)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return;

        bool needsRaise = (GetWindowLongPtr(hwnd, ExtendedStyle).ToInt64() & TopmostStyle) == 0;
        if (!needsRaise)
        {
            EnumWindows((other, _) =>
            {
                if (other == hwnd) return false;
                if (!IsWindowVisible(other) || IsIconic(other)) return true;
                GetWindowThreadProcessId(other, out uint process);
                // Chat, settings and other MIA windows can intentionally overlap the pet.
                if (process == (uint)Environment.ProcessId) return true;
                if (DwmGetWindowAttributeInt(other, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                if (DwmGetWindowAttribute(other, 9, out var bounds, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(other, out bounds)) return true;
                needsRaise = bounds.Left < petBounds.Right && bounds.Right > petBounds.Left
                    && bounds.Top < petBounds.Bottom && bounds.Bottom > petBounds.Top;
                return !needsRaise;
            }, IntPtr.Zero);
        }

        if (needsRaise)
            SetWindowPos(hwnd, TopmostWindow, 0, 0, 0, 0, NoMove | NoSize | NoActivate | NoOwnerZOrder);
    }

    internal static void ConfigurePet(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        // Tool window plus NOACTIVATE: clicking the character never steals typing focus.
        SetWindowLongPtr(hwnd, -20, new IntPtr(style | 0x00000080L | 0x08000000L));
    }
    internal static void ConfigureHint(IntPtr hwnd)
    {
        long style=GetWindowLongPtr(hwnd,-20).ToInt64();
        SetWindowLongPtr(hwnd,-20,new IntPtr(style|0x80L|0x08000000L|0x20L));
    }
    internal static bool TrySurface(IntPtr hwnd, out Rect rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        if ((style & 0x80) != 0) return false;
        if (DwmGetWindowAttributeInt(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        if (DwmGetWindowAttribute(hwnd, 9, out rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(hwnd, out rect)) return false;
        return rect.Width >= 180 && rect.Height >= 100;
    }
    internal static List<Surface> Surfaces()
    {
        var result = new List<Surface>();
        EnumWindows((hwnd, _) => { if (TrySurface(hwnd, out var r)) result.Add(new Surface(hwnd, r)); return true; }, IntPtr.Zero);
        return result;
    }
    internal static bool IsExposed(Surface surface, double footX)
    {
        // A seated sprite overlaps the target's top pixels. Ignore our own overlay windows
        // when walking z-order, otherwise the pet would falsely occlude its own platform.
        int px=(int)footX,py=surface.Bounds.Top+8;bool exposed=false;
        EnumWindows((handle,_)=>
        {
            if(!IsWindowVisible(handle)||IsIconic(handle))return true;
            GetWindowThreadProcessId(handle,out uint pid);if(pid==(uint)Environment.ProcessId)return true;
            if(DwmGetWindowAttributeInt(handle,14,out int cloaked,sizeof(int))==0&&cloaked!=0)return true;
            if(DwmGetWindowAttribute(handle,9,out var r,Marshal.SizeOf<Rect>())!=0&&!GetWindowRect(handle,out r))return true;
            if(px<r.Left||px>=r.Right||py<r.Top||py>=r.Bottom)return true;
            exposed=handle==surface.Handle;return false;
        },IntPtr.Zero);
        return exposed;
    }
}
