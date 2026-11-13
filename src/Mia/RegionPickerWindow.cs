using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mia;

/// <summary>
/// Frozen-desktop selection surface in physical pixels. The supplied screenshot is borrowed.
/// Native placement plus AutoScaleMode.None avoids WPF DIP conversion across mixed-DPI screens.
/// </summary>
internal sealed class RegionPickerWindow : Form
{
    private readonly Bitmap desktop;
    private readonly Rectangle desktopBounds;
    private readonly Font labelFont = new("Microsoft JhengHei UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    private Point? dragStart;
    private bool drawing;
    public Rectangle Selection { get; private set; }

    public RegionPickerWindow(Bitmap screenshot, Rectangle bounds)
    {
        desktop = screenshot;
        desktopBounds = bounds;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "米亞 · 框選畫面（Esc 取消）";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, new IntPtr(-1), desktopBounds.X, desktopBounds.Y, desktopBounds.Width, desktopBounds.Height, 0x0040);
        Activate();
        Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(desktop, 0, 0);
        using var tint = new SolidBrush(Color.FromArgb(120, 10, 16, 25));
        e.Graphics.FillRectangle(tint, ClientRectangle);
        if (Selection.Width > 0 && Selection.Height > 0)
        {
            e.Graphics.DrawImage(desktop, Selection, Selection, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.FromArgb(255, 221, 153), 2);
            e.Graphics.DrawRectangle(pen, Selection);
        }
        var cursor = CursorPhysicalRelative();
        var x = Math.Clamp(cursor.X + 20, 10, Math.Max(10, ClientSize.Width - 400));
        var y = Math.Clamp(cursor.Y + 24, 10, Math.Max(10, ClientSize.Height - 48));
        using var background = new SolidBrush(Color.FromArgb(240, 24, 28, 36));
        e.Graphics.FillRectangle(background, x, y, 380, 32);
        using var foreground = new SolidBrush(Color.White);
        var message = drawing ? $"{Selection.Width} × {Selection.Height}　放開滑鼠完成 · Esc 取消" : "拖曳框選想讓米亞查看的區域 · Esc 取消";
        e.Graphics.DrawString(message, labelFont, foreground, x + 10, y + 7);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        dragStart = CursorPhysicalRelative();
        drawing = true;
        Capture = true;
        Selection = Rectangle.Empty;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drawing && dragStart is Point start)
        {
            var current = CursorPhysicalRelative();
            var raw = Rectangle.FromLTRB(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y), Math.Max(start.X, current.X), Math.Max(start.Y, current.Y));
            Selection = Rectangle.Intersect(raw, new Rectangle(0, 0, desktop.Width, desktop.Height));
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!drawing || e.Button != MouseButtons.Left) return;
        if (dragStart is Point start)
        {
            var current = CursorPhysicalRelative();
            Selection = Rectangle.Intersect(
                Rectangle.FromLTRB(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y), Math.Max(start.X, current.X), Math.Max(start.Y, current.Y)),
                new Rectangle(0, 0, desktop.Width, desktop.Height));
        }
        drawing = false;
        Capture = false;
        if (Selection.Width < 8 || Selection.Height < 8)
        {
            Selection = Rectangle.Empty;
            Invalidate();
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    private Point CursorPhysicalRelative()
    {
        GetCursorPos(out var point);
        return new Point(Math.Clamp(point.X - desktopBounds.X, 0, desktop.Width), Math.Clamp(point.Y - desktopBounds.Y, 0, desktop.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) labelFont.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
