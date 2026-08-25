using System;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics;

namespace WinCapRecorder.Capture
{
    public sealed class FrameArrivedEventArgs : EventArgs
    {
        // IMPORTANT: this is a CPU-owned copy. It is no longer a reference to a
        // WGC frame-pool texture, so the encoder never touches a texture after
        // TryGetNextFrame() / frame.Dispose().
        public byte[] Bgra { get; }
        public int Width { get; }
        public int Height { get; }

        public FrameArrivedEventArgs(byte[] bgra, int width, int height)
        {
            Bgra = bgra;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Captures a window with Windows Graphics Capture and converts each WGC
    /// frame to an owned CPU BGRA buffer before returning from the WGC callback.
    ///
    /// This deliberately keeps all D3D11 work on the WGC callback thread.
    /// The encoder thread never touches D3D11 resources/context, which avoids
    /// lifetime races, frame-pool texture reuse, and immediate-context races.
    /// </summary>
    public sealed class WindowCaptureSession : IDisposable
    {
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private ID3D11Device? _d3dDevice;
        private IDirect3DDevice? _winrtDevice;
        private ID3D11Texture2D? _stagingTexture;
        private int _stagingWidth;
        private int _stagingHeight;
        private readonly object _d3dLock = new();
        private readonly object _frameLock = new();
        private SizeInt32 _lastSize;
        private bool _stopped;
        private int _activeCallbacks;
        private readonly ManualResetEventSlim _callbacksIdle = new(true);

        public event EventHandler<FrameArrivedEventArgs>? FrameArrived;
        public event EventHandler<Exception>? FrameError;

        /// <summary>
        /// Raised (once) a few frames after Start() if the captured frame data
        /// looks like a solid/near-uniform color instead of real window content.
        /// This is not a code bug in the encoder pipeline - Windows Graphics
        /// Capture itself returns a blank frame in two well known situations:
        ///  1) The target window belongs to a process running at a HIGHER
        ///     integrity level than this app (e.g. target runs "as
        ///     Administrator" while this app runs as a normal user).
        ///  2) The target window is showing DRM/protected video content
        ///     (Windows deliberately blanks capture of protected surfaces).
        /// </summary>
        public event EventHandler<string>? CaptureWarning;
        public int Width { get; private set; }
        public int Height { get; private set; }
        private int _diagnosticFrameCount;
        private bool _diagnosticReported;

        public bool Start(IntPtr hwnd)
        {
            if (!WgcInterop.IsSupported())
                throw new PlatformNotSupportedException("이 Windows 환경에서는 Windows Graphics Capture를 사용할 수 없습니다.");

            _item = WgcInterop.CreateItemForWindow(hwnd);
            if (_item == null) return false;

            _d3dDevice = D3D11Helper.CreateD3DDevice(out _);
            _winrtDevice = D3D11Helper.CreateWinRtDevice(_d3dDevice);

            Width = _item.Size.Width;
            Height = _item.Size.Height;
            _lastSize = _item.Size;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            try
            {
                var borderProp = _session.GetType().GetProperty("IsBorderRequired");
                borderProp?.SetValue(_session, false);
            }
            catch { }

            _session.StartCapture();
            _stopped = false;
            return true;
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            Interlocked.Increment(ref _activeCallbacks);
            _callbacksIdle.Reset();
            try
            {
                lock (_frameLock)
                {
                    if (_stopped) return;

                    using var frame = sender.TryGetNextFrame();
                    if (frame == null) return;

                    var contentSize = frame.ContentSize;
                    if (contentSize.Width <= 0 || contentSize.Height <= 0) return;

                // The surface belongs to the CURRENT frame-pool allocation.
                // When the window changes size, the current frame can still be
                // backed by the OLD pool size. Do not call Recreate until after
                // this frame has been copied out. Microsoft explicitly notes
                // that Recreate discards existing frames.
                var poolSize = _lastSize;

                // Resolve the native ID3D11Texture2D* via CsWinRT interop
                // (NOT Marshal.GetIUnknownForObject — that path throws E_NOINTERFACE).
                IntPtr nativeTex = D3D11Helper.GetNativeTexturePointer(frame.Surface);
                byte[] bgra;
                ID3D11Texture2D? source = null;
                try
                {
                    try
                    {
                        source = D3D11Helper.WrapTexture(nativeTex);
                        nativeTex = IntPtr.Zero; // ownership transferred to Vortice
                    }
                    catch (Exception wrapEx)
                    {
                        throw new InvalidOperationException(
                            "네이티브 텍스처 포인터를 Vortice 래퍼로 감싸지 못했습니다: " + wrapEx.Message,
                            wrapEx);
                    }

                    var desc = source.Description;
                    int texW = (int)desc.Width;
                    int texH = (int)desc.Height;
                    if (texW <= 0 || texH <= 0)
                    {
                        texW = poolSize.Width;
                        texH = poolSize.Height;
                    }
                    if (texW <= 0 || texH <= 0)
                        return;

                    var full = ReadbackToCpu(source, texW, texH);

                    int cropW = Math.Min(contentSize.Width, texW);
                    int cropH = Math.Min(contentSize.Height, texH);
                    if (cropW <= 0 || cropH <= 0)
                        return;

                    if (cropW == texW && cropH == texH)
                        bgra = full;
                    else
                        bgra = CropBgra(full, texW, texH, cropW, cropH);

                    contentSize = new SizeInt32 { Width = cropW, Height = cropH };
                }
                finally
                {
                    try { source?.Dispose(); } catch { }
                    if (nativeTex != IntPtr.Zero)
                    {
                        try { System.Runtime.InteropServices.Marshal.Release(nativeTex); } catch { }
                    }
                }

                // Only after the current frame's surface has been completely
                // copied and released is it safe to recreate the frame pool.
                if (contentSize.Width != _lastSize.Width ||
                    contentSize.Height != _lastSize.Height)
                {
                    _lastSize = contentSize;
                    Width = contentSize.Width;
                    Height = contentSize.Height;

                    sender.Recreate(
                        _winrtDevice!,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        contentSize);
                }

                    CheckForBlankFrame(bgra);

                    FrameArrived?.Invoke(
                        this,
                        new FrameArrivedEventArgs(
                            bgra,
                            contentSize.Width,
                            contentSize.Height));
                }
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                        $"[{DateTime.Now:O}] WGC_FRAME: {ex}");
                }
                catch { }

                try { FrameError?.Invoke(this, ex); } catch { }
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeCallbacks) == 0)
                    _callbacksIdle.Set();
            }
        }

        // Cheap sampled check: reads ~2000 pixels spread across the frame instead
        // of the whole buffer, so it costs nothing on the hot capture path.
        // Waits a handful of frames before judging, so the very first frame
        // (which can legitimately still be compositing) doesn't trigger a
        // false positive.
        private void CheckForBlankFrame(byte[] bgra)
        {
            if (_diagnosticReported) return;
            if (++_diagnosticFrameCount < 15) return;
            _diagnosticReported = true;

            if (bgra.Length < 4) return;

            const int sampleTarget = 2000;
            int pixelCount = bgra.Length / 4;
            int step = Math.Max(1, pixelCount / sampleTarget);

            long sum = 0;
            byte min = 255, max = 0;
            int samples = 0;
            for (int i = 0; i < pixelCount; i += step)
            {
                int p = i * 4;
                byte b = bgra[p];
                byte g = bgra[p + 1];
                byte r = bgra[p + 2];
                byte lo = Math.Min(b, Math.Min(g, r));
                byte hi = Math.Max(b, Math.Max(g, r));
                if (lo < min) min = lo;
                if (hi > max) max = hi;
                sum += b + g + r;
                samples++;
            }

            if (samples == 0) return;
            double avg = sum / (double)(samples * 3);

            // Effectively a single flat color across the whole sampled frame.
            bool isFlat = (max - min) <= 4;
            if (!isFlat) return;

            string message = avg >= 240
                ? "캡처된 화면이 흰색 빈 화면으로 보입니다. 대상 창이 관리자 권한으로 실행 중이라면 이 프로그램도 관리자 권한으로 다시 실행해보세요. DRM으로 보호된 동영상 재생 화면은 Windows 정책상 캡처할 수 없습니다."
                : avg <= 10
                    ? "캡처된 화면이 검은색 빈 화면으로 보입니다. 대상 창이 관리자 권한으로 실행 중이라면 이 프로그램도 관리자 권한으로 다시 실행해보세요. 하드웨어 오버레이를 사용하는 일부 게임/동영상 재생 화면은 캡처할 수 없습니다."
                    : "캡처된 화면이 단색으로 보입니다. 대상 창이 화면에 정상적으로 그려지고 있는지 확인해주세요.";

            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:O}] CAPTURE_BLANK_WARNING avg={avg:F1} min={min} max={max}: {message}\r\n\r\n");
            }
            catch { }

            try { CaptureWarning?.Invoke(this, message); } catch { }
        }

        private static byte[] CropBgra(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int width,
            int height)
        {
            int sourceRowBytes = checked(sourceWidth * 4);
            int rowBytes = checked(width * 4);
            byte[] result = new byte[checked(rowBytes * height)];

            int rows = Math.Min(sourceHeight, height);
            for (int y = 0; y < rows; y++)
            {
                Buffer.BlockCopy(
                    source,
                    y * sourceRowBytes,
                    result,
                    y * rowBytes,
                    rowBytes);
            }

            return result;
        }

        private byte[] ReadbackToCpu(ID3D11Texture2D source, int width, int height)
        {
            lock (_d3dLock)
            {
                if (_d3dDevice == null)
                    throw new ObjectDisposedException(nameof(WindowCaptureSession));

                if (_stagingTexture == null || _stagingWidth != width || _stagingHeight != height)
                {
                    _stagingTexture?.Dispose();

                    var desc = new Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };

                    _stagingTexture = _d3dDevice.CreateTexture2D(desc);
                    _stagingWidth = width;
                    _stagingHeight = height;
                }

                var context = _d3dDevice.ImmediateContext;
                context.CopyResource(_stagingTexture, source);

                Vortice.Direct3D11.MappedSubresource mapped;
                try
                {
                    mapped = context.Map(
                        _stagingTexture,
                        0,
                        MapMode.Read,
                        Vortice.Direct3D11.MapFlags.None);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"D3D11 프레임 읽기(Map) 실패: {ex.Message}", ex);
                }

                int rowBytes = checked(width * 4);
                byte[] result = new byte[checked(rowBytes * height)];

                try
                {
                    unsafe
                    {
                        byte* src = (byte*)mapped.DataPointer;
                        fixed (byte* dst = result)
                        {
                            for (int y = 0; y < height; y++)
                            {
                                Buffer.MemoryCopy(
                                    src + y * mapped.RowPitch,
                                    dst + y * rowBytes,
                                    rowBytes,
                                    rowBytes);
                            }
                        }
                    }
                }
                finally
                {
                    context.Unmap(_stagingTexture, 0);
                }

                return result;
            }
        }

        public void Stop()
        {
            _stopped = true;

            // Stop delivery first. A FrameArrived callback may already be running,
            // so do not release any D3D object until every callback has returned.
            try
            {
                if (_framePool != null)
                    _framePool.FrameArrived -= OnFrameArrived;
            }
            catch { }

            try { _session?.Dispose(); } catch { }
            try { _framePool?.Dispose(); } catch { }

            try
            {
                _callbacksIdle.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }

            lock (_d3dLock)
            {
                try { _stagingTexture?.Dispose(); } catch { }
                _stagingTexture = null;
                _stagingWidth = 0;
                _stagingHeight = 0;
            }

            _session = null;
            _framePool = null;
            _winrtDevice = null;
        }

        public void Dispose()
        {
            Stop();
            try { _d3dDevice?.Dispose(); } catch { }
            _d3dDevice = null;
        }
    }
}
