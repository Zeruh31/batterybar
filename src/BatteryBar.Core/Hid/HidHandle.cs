using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar.Core.Hid;

/// <summary>
/// A shared (non-exclusive) read/write handle to one HID collection with overlapped I/O,
/// so reads can be waited on with a timeout and alongside other collections.
/// </summary>
internal sealed unsafe class HidHandle : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly byte* _readBuffer;
    private readonly byte* _writeBuffer;
    private readonly NativeOverlapped* _readOverlapped;
    private readonly NativeOverlapped* _writeOverlapped;
    private readonly ManualResetEvent _readEvent = new(false);
    private readonly ManualResetEvent _writeEvent = new(false);
    private bool _readPending;
    private bool _disposed;

    private HidHandle(SafeFileHandle handle, HidCollectionInfo info)
    {
        _handle = handle;
        Info = info;
        _readBuffer = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(info.InputReportLength, 1));
        _writeBuffer = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(info.OutputReportLength, 1));
        _readOverlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        _writeOverlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
    }

    public HidCollectionInfo Info { get; }

    public WaitHandle ReadEvent => _readEvent;

    public static HidHandle? Open(HidCollectionInfo info)
    {
        // Shared access: G HUB and other tools keep their own handles open and keep working.
        SafeFileHandle handle = HidNative.CreateFileW(info.Path, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE, IntPtr.Zero, HidNative.OPEN_EXISTING,
            HidNative.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Log.Debug($"open failed ({Marshal.GetLastWin32Error()}): {info.Path}");
            handle.Dispose();
            return null;
        }

        return new HidHandle(handle, info);
    }

    /// <summary>Queues a read if none is pending. ReadEvent is signaled when a report has arrived.</summary>
    public bool StartRead()
    {
        if (_readPending)
        {
            return true;
        }

        *_readOverlapped = default;
        _readOverlapped->EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle();
        if (HidNative.ReadFile(_handle, _readBuffer, Info.InputReportLength, IntPtr.Zero, _readOverlapped)
            || Marshal.GetLastWin32Error() == HidNative.ERROR_IO_PENDING)
        {
            // On an overlapped handle the event is set on synchronous completion too.
            _readPending = true;
            return true;
        }

        return false;
    }

    /// <summary>Collects the report of a read whose event is signaled.</summary>
    public byte[]? CompleteRead()
    {
        if (!_readPending)
        {
            return null;
        }

        _readPending = false;
        if (!HidNative.GetOverlappedResult(_handle, _readOverlapped, out int read, false) || read <= 0)
        {
            return null;
        }

        return new ReadOnlySpan<byte>(_readBuffer, read).ToArray();
    }

    public bool Write(ReadOnlySpan<byte> report, int timeoutMs)
    {
        int length = Info.OutputReportLength;
        if (report.Length > length)
        {
            return false;
        }

        var target = new Span<byte>(_writeBuffer, length);
        target.Clear();
        report.CopyTo(target);

        *_writeOverlapped = default;
        _writeOverlapped->EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle();
        if (!HidNative.WriteFile(_handle, _writeBuffer, length, IntPtr.Zero, _writeOverlapped))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != HidNative.ERROR_IO_PENDING)
            {
                Log.Debug($"write failed ({error}) on {Info.Path}");
                return false;
            }

            if (!_writeEvent.WaitOne(timeoutMs))
            {
                HidNative.CancelIoEx(_handle, _writeOverlapped);
                HidNative.GetOverlappedResult(_handle, _writeOverlapped, out _, true);
                return false;
            }
        }

        return HidNative.GetOverlappedResult(_handle, _writeOverlapped, out _, false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_readPending && !_handle.IsInvalid)
        {
            // The kernel still owns the buffer until the cancelled read has finished.
            HidNative.CancelIoEx(_handle, _readOverlapped);
            HidNative.GetOverlappedResult(_handle, _readOverlapped, out _, true);
            _readPending = false;
        }

        _handle.Dispose();
        NativeMemory.Free(_readBuffer);
        NativeMemory.Free(_writeBuffer);
        NativeMemory.Free(_readOverlapped);
        NativeMemory.Free(_writeOverlapped);
        _readEvent.Dispose();
        _writeEvent.Dispose();
    }
}
