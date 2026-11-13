using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingRectangle = System.Drawing.Rectangle;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using WpfButton = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;

namespace Mia;

/// <summary>
/// Explicit, one-shot visible-desktop capture. Call from the UI thread after hiding Mia's windows.
/// No images are written to disk. Occluded content is NOT reconstructed, and protected windows
/// may appear black. The app manifest must opt into PerMonitorV2 DPI awareness.
/// </summary>
public static class CaptureService
{
    private const int MaxImageEdge = 1600;
    private const long MaxCapturePixels = 64_000_000;
    private static CaptureLifetime? activeCapture;

    public static Task<byte[]?> CaptureRegionAsync(CancellationToken cancellationToken = default)
        => RunCaptureAsync(CaptureRegionCoreAsync, cancellationToken);

    public static Task<byte[]?> CaptureWindowAsync(CancellationToken cancellationToken = default)
        => RunCaptureAsync(CaptureWindowCoreAsync, cancellationToken);

    /// <summary>Cancel pending capture delays and close either picker. Call on the UI thread before app shutdown.</summary>
    public static void CancelActive()
    {
        RequireUiThread();
        activeCapture?.Cancel();
    }

    private static async Task<byte[]?> RunCaptureAsync(Func<CaptureLifetime, Task<byte[]?>> capture, CancellationToken cancellationToken)
    {
        RequireUiThread();
        CancelActive();
        using var lifetime = new CaptureLifetime(cancellationToken);
        activeCapture = lifetime;
        try
        {
            lifetime.EnsureActive();
            var result = await capture(lifetime);
            lifetime.EnsureActive();
            return result;
        }
        catch (OperationCanceledException) when (lifetime.IsCancelled)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(activeCapture, lifetime)) activeCapture = null;
        }
    }

    private static async Task<byte[]?> CaptureRegionCoreAsync(CaptureLifetime lifetime)
    {
        // Let the compositor remove the caller's now-hidden windows.
        await Task.Delay(180, lifetime.Token);
        lifetime.EnsureActive();
        var bounds = VirtualBounds();
        using var desktop = CaptureBitmap(bounds);
        using var picker = new RegionPickerWindow(desktop, bounds);
        lifetime.EnsureActive();
        lifetime.ClosePicker = () => { if (!picker.IsDisposed) picker.Close(); };
        System.Windows.Forms.DialogResult result;
        try { result = picker.ShowDialog(); }
        finally { lifetime.ClosePicker = null; }
        lifetime.EnsureActive();
        if (result != System.Windows.Forms.DialogResult.OK || picker.Selection.Width < 8 || picker.Selection.Height < 8)
            return null;
        using var selected = desktop.Clone(picker.Selection, PixelFormat.Format32bppArgb);
        return Encode(selected);
    }

    private static async Task<byte[]?> CaptureWindowCoreAsync(CaptureLifetime lifetime)
    {
        lifetime.EnsureActive();
        var windows = EnumerateWindows();
        if (windows.Count == 0)
            throw new InvalidOperationException("目前沒有可擷取的可見視窗。請先開啟要讓米亞查看的內容。");

        var dialog = new Window
        {
            Title = "米亞 · 選擇要查看的視窗", Width = 560, Height = 420,
            MinWidth = 440, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 29, 38)),
            Foreground = WpfBrushes.White, FontFamily = new System.Windows.Media.FontFamily("Microsoft JhengHei UI"),
            Topmost = true, ShowInTaskbar = false
        };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var intro = new TextBlock
        {
            Text = "只擷取所選視窗在螢幕上可見的範圍。遮擋物也會入鏡；受保護內容可能呈黑色。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        };
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);
        var list = new ListBox
        {
            ItemsSource = windows, DisplayMemberPath = nameof(WindowChoice.Title), SelectedIndex = 0,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 40, 52)),
            Foreground = WpfBrushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(8)
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new WpfButton { Content = "取消", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0) };
        var accept = new WpfButton { Content = "擷取這個視窗", IsDefault = true, Padding = new Thickness(18, 8, 18, 8) };
        accept.Click += (_, _) => { if (list.SelectedItem != null) dialog.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) dialog.DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        dialog.Content = panel;
        lifetime.EnsureActive();
        lifetime.ClosePicker = dialog.Close;
        bool? result;
        try { result = dialog.ShowDialog(); }
        finally { lifetime.ClosePicker = null; }
        lifetime.EnsureActive();
        if (result != true || list.SelectedItem is not WindowChoice choice) return null;

        if (!IsWindowVisible(choice.Handle) || IsIconic(choice.Handle))
            throw new InvalidOperationException("所選視窗已關閉或最小化，請重新選擇。");
        SetForegroundWindow(choice.Handle);
        await Task.Delay(250, lifetime.Token);
        lifetime.EnsureActive();
        if (!IsWindowVisible(choice.Handle) || IsIconic(choice.Handle) || !TryBounds(choice.Handle, out var rect))
            throw new InvalidOperationException("無法取得視窗位置，請重新選擇。");
        rect = DrawingRectangle.Intersect(rect, VirtualBounds());
        if (rect.Width < 8 || rect.Height < 8)
            throw new InvalidOperationException("所選視窗不在可見螢幕範圍內。");
        using var image = CaptureBitmap(rect);
        return Encode(image);
    }

    private sealed class CaptureLifetime : IDisposable
    {
        private readonly CancellationTokenSource source;
        private readonly CancellationTokenRegistration registration;
        private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        private bool disposed;
        public Action? ClosePicker { get; set; }
        public CancellationToken Token => source.Token;
        public bool IsCancelled => source.IsCancellationRequested || dispatcher.HasShutdownStarted;

        public CaptureLifetime(CancellationToken cancellationToken)
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            registration = source.Token.Register(() =>
            {
                if (dispatcher.CheckAccess()) CloseCurrentPicker();
                else if (!dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvoke(new Action(CloseCurrentPicker));
            });
        }

        private void CloseCurrentPicker()
        {
            if (disposed) return;
            var close = ClosePicker;
            ClosePicker = null;
            close?.Invoke();
        }

        public void Cancel()
        {
            if (!disposed) source.Cancel();
        }

        public void EnsureActive()
        {
            if (dispatcher.HasShutdownStarted) Cancel();
            source.Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            disposed = true;
            ClosePicker = null;
            registration.Dispose();
            source.Dispose();
        }
    }

    private static void RequireUiThread()
    {
        if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
            throw new InvalidOperationException("畫面選取必須從介面執行緒啟動。");
    }

    internal static DrawingRectangle VirtualBounds() => new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    private static Bitmap CaptureBitmap(DrawingRectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > MaxCapturePixels)
            throw new InvalidOperationException("擷取範圍過大或無效。請改用單一視窗擷取。");
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static System.Drawing.Size FitImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        double ratio = Math.Min(1d, (double)MaxImageEdge / Math.Max(width, height));
        return new System.Drawing.Size(Math.Max(1, (int)Math.Round(width * ratio)), Math.Max(1, (int)Math.Round(height * ratio)));
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        var size = FitImage(bitmap.Width, bitmap.Height);
        using var stream = new MemoryStream();
        if (size.Width == bitmap.Width && size.Height == bitmap.Height)
            bitmap.Save(stream, ImageFormat.Png);
        else
        {
            using var resized = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(bitmap, new DrawingRectangle(0, 0, size.Width, size.Height));
            }
            resized.Save(stream, ImageFormat.Png);
        }
        return stream.ToArray();
    }

    private static List<WindowChoice> EnumerateWindows()
    {
        var choices = new List<WindowChoice>();
        var ownPid = (uint)Environment.ProcessId;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == ownPid) return true;
            if (DwmGetWindowAttributeInt(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(1024);
            GetWindowText(handle, title, title.Capacity);
            if (string.IsNullOrWhiteSpace(title.ToString()) || !TryBounds(handle, out var bounds) || bounds.Width < 8 || bounds.Height < 8) return true;
            choices.Add(new WindowChoice(handle, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return choices;
    }

    private static bool TryBounds(IntPtr handle, out DrawingRectangle rectangle)
    {
        // DWM extended frame bounds are physical pixels, unlike DPI-virtualized GetWindowRect.
        if (DwmGetWindowAttributeRect(handle, 9, out var bounds, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(handle, out bounds))
        { rectangle = DrawingRectangle.Empty; return false; }
        rectangle = DrawingRectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        return rectangle.Width > 0 && rectangle.Height > 0;
    }

    private sealed record WindowChoice(IntPtr Handle, string Title);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);
}
