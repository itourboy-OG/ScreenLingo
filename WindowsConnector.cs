using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace ScreenLingo;

/// <summary>Connects to native window selection, hotkeys, physical screen coordinates and overlay behavior.</summary>
public static class WindowsConnector
{
    public static CaptureTarget? ForegroundTarget()
    {
        nint window = GetForegroundWindow();
        return window == 0 ? null : WindowTarget(window);
    }

    public static CaptureTarget? PointerWindowTarget()
    {
        if (!GetCursorPos(out NativePoint point)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the pointer position.");
        nint window = RootWindowAt(point.X, point.Y);
        return window == 0 ? null : WindowTarget(window);
    }

    public static nint RootWindowAt(int x, int y) => GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2);

    public static CaptureTarget PointerMonitorTarget()
    {
        if (!GetCursorPos(out NativePoint point)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the pointer position.");
        nint monitor = MonitorFromPoint(point, 2);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the monitor bounds.");
        return new CaptureTarget(monitor, CaptureKind.Monitor, ToRect(info.Monitor), "Monitor under pointer");
    }

    public static CaptureTarget? WindowTarget(nint window)
    {
        if (!IsWindow(window) || IsIconic(window)) return null;
        uint thread = GetWindowThreadProcessId(window, out uint process);
        if (thread == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not identify the active application.");
        if (process == Environment.ProcessId) return null;
        return TestWindowTarget(window);
    }

    public static CaptureTarget TestWindowTarget(nint window)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(window, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()));
        StringBuilder title = new(512);
        _ = GetWindowText(window, title, title.Capacity);
        return new CaptureTarget(window, CaptureKind.Window, ToRect(bounds), title.Length == 0 ? "Untitled window" : title.ToString());
    }

    public static void ExcludeFromCapture(nint window)
    {
        if (!SetWindowDisplayAffinity(window, 0x11))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not exclude ScreenLingo from capture. Windows 10 version 2004 or later is required.");
    }

    public static void MakeOverlay(nint window)
    {
        nint styles = GetWindowLongPtr(window, -20);
        Marshal.SetLastPInvokeError(0);
        nint previous = SetWindowLongPtr(window, -20, styles | 0x20 | 0x08000000 | 0x80);
        if (previous == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not make the translation overlay click-through.");
        _ = EnableWindow(window, false);
        ExcludeFromCapture(window);
    }

    public static void PositionOverlay(nint window, PixelRect bounds)
    {
        if (!SetWindowPos(window, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not position the translation overlay.");
    }

    public static void RegisterShortcut(nint window, int id, uint modifiers, uint key)
    {
        if (!RegisterHotKey(window, id, modifiers | 0x4000, key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register keyboard shortcut {id}. Another application may already use Ctrl+Alt+{(char)key}.");
    }

    public static void RemoveShortcut(nint window, int id)
    {
        if (!UnregisterHotKey(window, id)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not unregister shortcut {id}.");
    }

    private static PixelRect ToRect(NativeRect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out NativeRect result, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll")] private static extern bool EnableWindow(nint window, bool enable);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint window, int id);
}

/// <summary>Captures GPU-rendered windows or monitors through Windows Graphics Capture, without injecting into the game.</summary>
public sealed class CaptureConnector : IDisposable
{
    private readonly IDirect3DDevice device;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private readonly object frameGate = new();
    private readonly TaskCompletionSource<bool> firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Direct3D11CaptureFrame? latestFrame;
    private Exception? captureFailure;
    private bool disposed;

    private CaptureConnector(GraphicsCaptureItem item)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Windows Graphics Capture is unavailable on this PC.");
        if (item.Size.Width <= 0 || item.Size.Height <= 0) throw new InvalidOperationException("The selected application has no visible capture area.");
        device = CreateDevice();
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        pool.FrameArrived += FrameArrived;
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.StartCapture();
    }

    public static CaptureConnector ForWindow(CaptureTarget target) => new(CreateWindowItem(target.Handle));
    public static CaptureConnector ForMonitor(CaptureTarget target) => new(CreateMonitorItem(target.Handle));

    public async Task<CapturedImage> SnapshotAsync(CancellationToken cancellation)
    {
        _ = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellation);
        using SoftwareBitmap bitmap = await Task.Run(CopyLatestBitmap, cancellation);
            using InMemoryRandomAccessStream stream = new();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellation);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellation);
            stream.Seek(0);
            using DataReader reader = new(stream.GetInputStreamAt(0));
            uint length = checked((uint)stream.Size);
            _ = await reader.LoadAsync(length).AsTask(cancellation);
            byte[] png = new byte[length];
            reader.ReadBytes(png);
            return new CapturedImage(png, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private SoftwareBitmap CopyLatestBitmap()
    {
        lock (frameGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (captureFailure is not null) throw new InvalidOperationException("Windows graphics capture failed. Re-enable translation to restart it.", captureFailure);
            Direct3D11CaptureFrame frame = latestFrame ?? throw new InvalidOperationException("Windows has not provided a capture frame.");
            // WGC emits changed frames only. Retain the last GPU frame so static menus remain readable.
            return SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
        }
    }

    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            lock (frameGate)
            {
                if (disposed) return;
                Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
                if (frame is null) return;
                latestFrame?.Dispose();
                latestFrame = frame;
                firstFrame.TrySetResult(true);
            }
        }
        catch (COMException error)
        {
            captureFailure = error;
            firstFrame.TrySetException(error);
        }
    }

    private static GraphicsCaptureItem CreateWindowItem(nint window)
    {
        Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        nint pointer = GraphicsCaptureItem.As<ICaptureItemInterop>().CreateForWindow(window, ref iid);
        try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static GraphicsCaptureItem CreateMonitorItem(nint monitor)
    {
        Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        nint pointer = GraphicsCaptureItem.As<ICaptureItemInterop>().CreateForMonitor(monitor, ref iid);
        try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out nint nativeDevice, out _, out nint context));
        try
        {
            Guid iid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in iid, out nint dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out nint inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(context); Marshal.Release(nativeDevice); }
    }

    public void Dispose()
    {
        pool.FrameArrived -= FrameArrived;
        lock (frameGate)
        {
            if (disposed) return;
            disposed = true;
            firstFrame.TrySetCanceled();
            latestFrame?.Dispose();
            latestFrame = null;
            session.Dispose();
            pool.Dispose();
            device.Dispose();
        }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint count, uint sdk, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);
}
