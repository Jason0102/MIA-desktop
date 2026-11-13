using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Mia.Services;

// ElevenLabs pcm_24000: signed 16-bit little-endian, mono, no WAV header.
internal static class PcmStreamPlayer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Format { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr device, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr device);
    private static void Check(uint result) { if (result != 0) throw new IOException("Windows 無法播放串流語音。"); }

    public static async Task PlayAsync(Stream stream, Action started, CancellationToken ct)
    {
        var format = new Format { Tag = 1, Channels = 1, Rate = 24000, BytesPerSecond = 48000, Align = 2, Bits = 16 };
        Check(waveOutOpen(out var device, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
        var buffers = new Queue<(IntPtr Header, IntPtr Data)>();
        uint size = (uint)Marshal.SizeOf<Header>();
        bool playing = false;
        try
        {
            var bytes = new byte[4096];
            while (true)
            {
                // Bound queued audio so a slow consumer cannot grow native memory indefinitely.
                while (buffers.Count >= 4) { ct.ThrowIfCancellationRequested(); if (!ReleaseFinished()) await Task.Delay(5, ct); }
                int count = await stream.ReadAsync(bytes, ct);
                if (count == 0) break;
                if ((count & 1) != 0)
                {
                    int extra = await stream.ReadAsync(bytes.AsMemory(count, 1), ct);
                    if (extra == 0) throw new IOException("串流音訊不完整。");
                    count++;
                }
                var data = Marshal.AllocHGlobal(count);
                var header = Marshal.AllocHGlobal((int)size);
                bool prepared = false;
                try
                {
                    Marshal.Copy(bytes, 0, data, count);
                    Marshal.StructureToPtr(new Header { Data = data, Length = (uint)count }, header, false);
                    Check(waveOutPrepareHeader(device, header, size)); prepared = true;
                    Check(waveOutWrite(device, header, size));
                    buffers.Enqueue((header, data));
                }
                catch { if (prepared) waveOutUnprepareHeader(device, header, size); Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); throw; }
                if (!playing) { playing = true; started(); }
            }
            if (!playing) throw new IOException("語音服務回傳空白音訊。");
            while (buffers.Count > 0) { ct.ThrowIfCancellationRequested(); if (!ReleaseFinished()) await Task.Delay(5, ct); }
        }
        finally
        {
            waveOutReset(device);
            while (buffers.TryDequeue(out var buffer)) { waveOutUnprepareHeader(device, buffer.Header, size); Marshal.FreeHGlobal(buffer.Header); Marshal.FreeHGlobal(buffer.Data); }
            waveOutClose(device);
        }
        bool ReleaseFinished()
        {
            var buffer = buffers.Peek();
            if ((Marshal.PtrToStructure<Header>(buffer.Header).Flags & 1) == 0) return false;
            Check(waveOutUnprepareHeader(device, buffer.Header, size));
            buffers.Dequeue(); Marshal.FreeHGlobal(buffer.Header); Marshal.FreeHGlobal(buffer.Data); return true;
        }
    }
}
