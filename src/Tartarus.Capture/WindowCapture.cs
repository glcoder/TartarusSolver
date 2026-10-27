using System.Runtime.InteropServices;
using Tartarus.Vision;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Tartarus.Capture;

/// <summary>
/// Captures one window with Windows.Graphics.Capture (CreateForWindow: only that window's content, so an
/// overlay drawn above it never shows up in the frames). Frames arrive on a free-threaded pool; the latest
/// one is kept on the GPU and copied to the CPU only when <see cref="Grab"/> is called.
/// </summary>
public sealed unsafe class WindowCapture : IDisposable
{
    private const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly object _lock = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private Direct3D11CaptureFrame? _latest;
    private DateTimeOffset _latestAt;
    private ID3D11Texture2D? _staging;
    private volatile bool _disposed;

    public WindowCapture(IntPtr hwnd)
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        Hwnd = hwnd;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device!, out _context!).CheckError();
        _winrtDevice = CreateWinRtDevice(_device);
        _item = CreateItemForWindow(hwnd);
        _item.Closed += (_, _) => Closed?.Invoke();
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, Format, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        TryDisableBorder(_session);
        _session.StartCapture();
    }

    public IntPtr Hwnd { get; }
    public string DisplayName => _item.DisplayName;
    public long FrameCount { get; private set; }
    public DateTimeOffset LastFrameAt => _latestAt;

    /// <summary>Raised (on a capture thread) when the captured window closes.</summary>
    public event Action? Closed;

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        var frame = pool.TryGetNextFrame();
        if (frame == null) return;
        // Windows calls this holding the Direct3D device's lock, and Grab, holding ours, waits on that one for its
        // copy: waiting here for ours would deadlock the two for good (a player's AMD card, the window frozen at the
        // first scan). A frame that comes during a Grab is dropped instead; the next one does.
        if (!Monitor.TryEnter(_lock))
        {
            frame.Dispose();
            return;
        }
        try
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }
            _latest?.Dispose();
            _latest = frame;
            _latestAt = DateTimeOffset.Now;
            FrameCount++;
            var size = frame.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                // The window was resized: new buffers of the new size. The frame in hand stays readable.
                _poolSize = size;
                pool.Recreate(_winrtDevice, Format, 2, size);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>
    /// Crop frames to the window's client area (what the game draws, without title bar and borders).
    /// Screen layout is computed from the client size, so this is on by default.
    /// </summary>
    public bool CropToClient { get; set; } = true;

    /// <summary>Where the last grabbed frame sits on screen (physical pixels): the client area, or the whole window.</summary>
    public PixelRect LastBounds { get; private set; }

    /// <summary>Copies the latest frame to a CPU buffer (BGRA). Null until the first frame arrives.</summary>
    public Frame? Grab()
    {
        lock (_lock)
        {
            if (_disposed || _latest == null) return null;
            using var texture = TextureOf(_latest.Surface);
            var desc = texture.Description;
            int fw = Math.Min(_latest.ContentSize.Width, (int)desc.Width);
            int fh = Math.Min(_latest.ContentSize.Height, (int)desc.Height);
            if (fw <= 0 || fh <= 0) return null;

            // The frame shows the window's visible bounds; the client area sits inside at some offset.
            var visible = Win32Windows.VisibleBounds(Hwnd);
            int x0 = 0, y0 = 0, w = fw, h = fh;
            LastBounds = new PixelRect(visible.Left, visible.Top, visible.Left + fw, visible.Top + fh);
            if (CropToClient && Win32Windows.ClientBounds(Hwnd) is { IsEmpty: false } client
                && visible.Width == fw && visible.Height == fh)
            {
                int cx = client.Left - visible.Left, cy = client.Top - visible.Top;
                if (cx >= 0 && cy >= 0 && cx + client.Width <= fw && cy + client.Height <= fh)
                {
                    (x0, y0, w, h) = (cx, cy, client.Width, client.Height);
                    LastBounds = client;
                }
            }

            if (_staging == null || _staging.Description.Width != w || _staging.Description.Height != h)
            {
                _staging?.Dispose();
                _staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                });
            }
            _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(x0, y0, 0, x0 + w, y0 + h, 1));
            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[w * h * 4];
                int row = w * 4;
                fixed (byte* dst = pixels)
                {
                    byte* src = (byte*)map.DataPointer;
                    for (int y = 0; y < h; y++)
                        Buffer.MemoryCopy(src + (long)y * map.RowPitch, dst + (long)y * row, row, row);
                }
                return new Frame(w, h, pixels, PixelFormat.Bgra32, _latestAt);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
        }
    }

    public void Dispose()
    {
        // A Grab stuck in the graphics driver holds the lock: its device is left to it, the caller doesn't wait too.
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(1)))
        {
            _disposed = true;
            _pool.FrameArrived -= OnFrameArrived;
            _session.Dispose();
            return;
        }
        try
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        _staging?.Dispose();
        _winrtDevice.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        // Windows 11: no yellow border around the captured window. Unpackaged apps are allowed this;
        // on older systems the property doesn't exist and the border just stays.
        try
        {
            _ = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            session.IsBorderRequired = false;
        }
        catch { }
    }

    // ---------- Interop ----------

    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", IID_IGraphicsCaptureItemInterop);
        try
        {
            IntPtr interop = factory.ThisPtr;
            Guid iid = IID_GraphicsCaptureItem;
            IntPtr result;
            // IGraphicsCaptureItemInterop::CreateForWindow is the first method after IUnknown's three.
            var createForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)interop + 3));
            Marshal.ThrowExceptionForHR(createForWindow(interop, hwnd, &iid, &result));
            try { return GraphicsCaptureItem.FromAbi(result); }
            finally { Marshal.Release(result); }
        }
        finally
        {
            factory.Dispose();
        }
    }

    private static ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        IntPtr abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            IntPtr access = QueryInterface(abi, IID_IDirect3DDxgiInterfaceAccess);
            try
            {
                Guid iid = IID_ID3D11Texture2D;
                IntPtr texture;
                // IDirect3DDxgiInterfaceAccess::GetInterface(REFIID, void**).
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)access + 3));
                Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally { Marshal.Release(access); }
        }
        finally { MarshalInterface<IDirect3DSurface>.DisposeAbi(abi); }
    }

    private static IntPtr QueryInterface(IntPtr unknown, Guid iid)
    {
        IntPtr result;
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)unknown));
        Marshal.ThrowExceptionForHR(qi(unknown, &iid, &result));
        return result;
    }
}
