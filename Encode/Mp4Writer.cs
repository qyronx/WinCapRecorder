using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace WinCapRecorder.Encode
{
    /// <summary>
    /// Media Foundation Sink Writer를 순수 P/Invoke로 직접 호출하여
    /// H.264(고비트레이트) + AAC MP4 기록.
    /// Vortice.MediaFoundation은 Sink Writer / IMFMediaType / IMFSample API를
    /// 바인딩하지 않으므로, mfplat.dll / mfreadwrite.dll을 직접 호출한다.
    /// 일시정지 시 새 프레임/샘플을 버려서 실제 녹화시간 기준으로 이어붙임.
    /// </summary>
    public sealed class Mp4Writer : IDisposable
    {
        // ---- GUIDs (mfapi.h / mfobjects.h / codecapi.h) ----
        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");

        private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644B4E48-1E66-11D2-8B0A-00A0C9A0E8A1");
        private static readonly Guid MF_MT_SAMPLE_SIZE = new("DAD3AB78-1990-408B-BCE2-E955E86A2ACB");
        private static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new("B8EBEFAF-B718-4E04-B0B4-5379C5A0D0F6");
        private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("C9173739-5E56-461C-B713-46FB995CB95F");
        private static readonly Guid MF_MT_AUDIO_CHANNEL_MASK = new("55FB5765-644A-4CAF-8479-938983BB7F3B");
        private static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");

        private const int MFVideoInterlace_Progressive = 2;

        // ---- P/Invoke: mfplat.dll ----
        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFCreateAttributes(out IMFAttributes attributes, int cInitialSize);

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFCreateMediaType(out IMFMediaType mediaType);

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer buffer);

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFCreateSample(out IMFSample sample);

        // ---- P/Invoke: mfreadwrite.dll ----
        [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string outputUrl,
            IntPtr byteStream,
            IMFAttributes? attributes,
            out IMFSinkWriter writer);

        private const int MF_VERSION = 0x00020070; // Win8+ (2.70)
        private const int MFSTARTUP_NOSOCKET = 0x1;

        // ---- COM interfaces ----
        [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int type);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
            [PreserveSig] int Compare(IMFAttributes other, int matchType, out bool result);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out long value);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double value);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
            [PreserveSig] int GetString(ref Guid guidKey, IntPtr value, int size, IntPtr length);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out int size);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buf, int bufSize, IntPtr blobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buf, out int size);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, int value);
            [PreserveSig] int SetUINT64(ref Guid guidKey, long value);
            [PreserveSig] int SetDouble(ref Guid guidKey, double value);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid value);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr buf, int size);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
            [PreserveSig] int CopyAllItems(IMFAttributes dest);
        }

        [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int type);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
            [PreserveSig] int Compare(IMFAttributes other, int matchType, out bool result);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out long value);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double value);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
            [PreserveSig] int GetString(ref Guid guidKey, IntPtr value, int size, IntPtr length);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out int size);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buf, int bufSize, IntPtr blobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buf, out int size);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, int value);
            [PreserveSig] int SetUINT64(ref Guid guidKey, long value);
            [PreserveSig] int SetDouble(ref Guid guidKey, double value);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid value);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr buf, int size);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
            [PreserveSig] int CopyAllItems(IMFAttributes dest);
        }

        [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out int length);
            [PreserveSig] int SetCurrentLength(int length);
            [PreserveSig] int GetMaxLength(out int length);
        }

        [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // Flatten IMFAttributes into this interface. This avoids relying on
            // managed COM interface inheritance for the native IMFSample vtable.
            [PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int GetItemType(ref Guid guidKey, out int type);
            [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
            [PreserveSig] int Compare(IMFAttributes other, int matchType, out bool result);
            [PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
            [PreserveSig] int GetUINT64(ref Guid guidKey, out long value);
            [PreserveSig] int GetDouble(ref Guid guidKey, out double value);
            [PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
            [PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
            [PreserveSig] int GetString(ref Guid guidKey, IntPtr value, int size, IntPtr length);
            [PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
            [PreserveSig] int GetBlobSize(ref Guid guidKey, out int size);
            [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buf, int bufSize, IntPtr blobSize);
            [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buf, out int size);
            [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
            [PreserveSig] int DeleteItem(ref Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, int value);
            [PreserveSig] int SetUINT64(ref Guid guidKey, long value);
            [PreserveSig] int SetDouble(ref Guid guidKey, double value);
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid value);
            [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr buf, int size);
            [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object value);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
            [PreserveSig] int CopyAllItems(IMFAttributes dest);

            [PreserveSig] int GetSampleFlags(out int flags);
            [PreserveSig] int SetSampleFlags(int flags);
            [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration(out long duration);
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(out int count);
            [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
            [PreserveSig] int RemoveBufferByIndex(int index);
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength(out int length);
            [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IMFMediaType targetMediaType, out int streamIndex);
            [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IMFAttributes? encodingParameters);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
            [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
            [PreserveSig] int PlaceMarker(int streamIndex, IntPtr contextValue);
            [PreserveSig] int NotifyEndOfSegment(int streamIndex);
            [PreserveSig] int Flush(int streamIndex);
            [PreserveSig] int FinalizeWriting();
            [PreserveSig] int GetServiceForStream(int streamIndex, ref Guid guidService, ref Guid riid, out IntPtr service);
            [PreserveSig] int GetStatistics(int streamIndex, IntPtr stats);
        }

        // ---- 인스턴스 상태 ----
        private IMFSinkWriter? _writer;
        private int _videoStreamIndex = -1;
        private int _audioStreamIndex = -1;
        private readonly long _videoFrameDuration; // 100ns units
        private long _videoTimestamp;
        private long _audioTimestamp;
        private readonly object _lock = new();
        private bool _started;
        private bool _mfStarted;
        private ID3D11Texture2D? _stagingTexture;
        private int _stagingWidth;
        private int _stagingHeight;

        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public bool HasAudio { get; }

        public Mp4Writer(string outputPath, int width, int height, int fps, bool hasAudio, long videoBitrateBps)
        {
            Width = width - (width % 2);
            Height = height - (height % 2);
            Fps = fps;
            HasAudio = hasAudio;
            _videoFrameDuration = 10_000_000L / Math.Max(fps, 1);

            ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET), "MFStartup");
            _mfStarted = true;

            ThrowIfFailed(MFCreateAttributes(out var attrs, 2), "MFCreateAttributes");
            ThrowIfFailed(attrs.SetUINT32(ref AsRef(MF_SINK_WRITER_DISABLE_THROTTLING), 1), "SetUINT32(DisableThrottling)");
            // Prefer the software H.264 encoder so our CPU-side NV12 samples are
            // accepted consistently. Hardware MFTs sometimes reject software NV12
            // samples and produce empty/white output without a clear HRESULT.
            var enableHw = new Guid("A7E025DD-DAC7-4830-A28E-1C0B0B32619D"); // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS
            ThrowIfFailed(attrs.SetUINT32(ref enableHw, 0), "SetUINT32(DisableHardwareTransforms)");

            ThrowIfFailed(MFCreateSinkWriterFromURL(outputPath, IntPtr.Zero, attrs, out _writer!), "MFCreateSinkWriterFromURL");
            ReleaseCom(attrs);

            // ---- Video output type: H.264 ----
            ThrowIfFailed(MFCreateMediaType(out var videoOut), "MFCreateMediaType(videoOut)");
            SetGuid(videoOut, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(videoOut, MF_MT_SUBTYPE, MFVideoFormat_H264);
            ThrowIfFailed(videoOut.SetUINT32(ref AsRef(MF_MT_AVG_BITRATE), (int)videoBitrateBps), "SetUINT32(AvgBitrate)");
            ThrowIfFailed(videoOut.SetUINT32(ref AsRef(MF_MT_INTERLACE_MODE), MFVideoInterlace_Progressive), "SetUINT32(Interlace)");
            SetPacked64(videoOut, MF_MT_FRAME_SIZE, (uint)Width, (uint)Height);
            SetPacked64(videoOut, MF_MT_FRAME_RATE, (uint)Fps, 1);
            SetPacked64(videoOut, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            ThrowIfFailed(_writer.AddStream(videoOut, out _videoStreamIndex), "AddStream(video)");
            ReleaseCom(videoOut);

            // ---- Video input type: NV12 ----
            // WGC supplies BGRA. We explicitly convert it to NV12 before handing it
            // to the H.264 encoder. This avoids relying on the SinkWriter's implicit
            // BGRA/RGB32 color converter, which can produce a valid MP4 containing
            // an all-white video on some Windows codec configurations.
            ThrowIfFailed(MFCreateMediaType(out var videoIn), "MFCreateMediaType(videoIn)");
            SetGuid(videoIn, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(videoIn, MF_MT_SUBTYPE, MFVideoFormat_NV12);
            ThrowIfFailed(videoIn.SetUINT32(ref AsRef(MF_MT_INTERLACE_MODE), MFVideoInterlace_Progressive), "SetUINT32(Interlace In)");
            SetPacked64(videoIn, MF_MT_FRAME_SIZE, (uint)Width, (uint)Height);
            SetPacked64(videoIn, MF_MT_FRAME_RATE, (uint)Fps, 1);
            SetPacked64(videoIn, MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            // Positive stride = top-down. For NV12 the Y-plane stride is Width.
            ThrowIfFailed(videoIn.SetUINT32(ref AsRef(MF_MT_DEFAULT_STRIDE), Width), "SetUINT32(DefaultStride In)");
            ThrowIfFailed(videoIn.SetUINT32(ref AsRef(MF_MT_SAMPLE_SIZE), checked(Width * Height * 3 / 2)), "SetUINT32(SampleSize In)");
            ThrowIfFailed(videoIn.SetUINT32(ref AsRef(MF_MT_FIXED_SIZE_SAMPLES), 1), "SetUINT32(FixedSizeSamples In)");
            ThrowIfFailed(videoIn.SetUINT32(ref AsRef(MF_MT_ALL_SAMPLES_INDEPENDENT), 1), "SetUINT32(AllSamplesIndependent In)");
            // Limited range (16-235) matches our BT.601 conversion.
            var nominalRange = new Guid("C21B8EE5-B956-4071-A06E-82E4E6E049A4");
            ThrowIfFailed(videoIn.SetUINT32(ref nominalRange, 1), "SetUINT32(NominalRange)"); // MFNominalRange_16_235

            ThrowIfFailed(_writer.SetInputMediaType(_videoStreamIndex, videoIn, null), "SetInputMediaType(video)");
            ReleaseCom(videoIn);

            // ---- Audio ----
            if (hasAudio)
            {
                ThrowIfFailed(MFCreateMediaType(out var audioOut), "MFCreateMediaType(audioOut)");
                SetGuid(audioOut, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                SetGuid(audioOut, MF_MT_SUBTYPE, MFAudioFormat_AAC);
                ThrowIfFailed(audioOut.SetUINT32(ref AsRef(MF_MT_AUDIO_BITS_PER_SAMPLE), 16), "Audio bits");
                ThrowIfFailed(audioOut.SetUINT32(ref AsRef(MF_MT_AUDIO_SAMPLES_PER_SECOND), 48000), "Audio sample rate");
                ThrowIfFailed(audioOut.SetUINT32(ref AsRef(MF_MT_AUDIO_NUM_CHANNELS), 2), "Audio channels");
                ThrowIfFailed(audioOut.SetUINT32(ref AsRef(MF_MT_AUDIO_CHANNEL_MASK), 3), "Audio channel mask");
                ThrowIfFailed(audioOut.SetUINT32(ref AsRef(MF_MT_AUDIO_AVG_BYTES_PER_SECOND), 24000), "Audio avg bytes/sec"); // 192 kbps AAC
                ThrowIfFailed(_writer.AddStream(audioOut, out _audioStreamIndex), "AddStream(audio)");
                ReleaseCom(audioOut);

                ThrowIfFailed(MFCreateMediaType(out var audioIn), "MFCreateMediaType(audioIn)");
                SetGuid(audioIn, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                SetGuid(audioIn, MF_MT_SUBTYPE, MFAudioFormat_PCM);
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_AUDIO_BITS_PER_SAMPLE), 16), "Audio in bits");
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_AUDIO_SAMPLES_PER_SECOND), 48000), "Audio in sample rate");
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_AUDIO_NUM_CHANNELS), 2), "Audio in channels");
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_AUDIO_CHANNEL_MASK), 3), "Audio in channel mask");
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_AUDIO_BLOCK_ALIGNMENT), 4), "Audio in block align");
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_AUDIO_AVG_BYTES_PER_SECOND), 48000 * 4), "Audio in avg bytes/sec");
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_FIXED_SIZE_SAMPLES), 1), "Audio in fixed samples");
                ThrowIfFailed(audioIn.SetUINT32(ref AsRef(MF_MT_ALL_SAMPLES_INDEPENDENT), 1), "Audio in independent samples");
                ThrowIfFailed(_writer.SetInputMediaType(_audioStreamIndex, audioIn, null), "SetInputMediaType(audio)");
                ReleaseCom(audioIn);
            }

            ThrowIfFailed(_writer.BeginWriting(), "BeginWriting");
            _started = true;
        }

        // Safe encoder path: receives an owned CPU BGRA frame. No D3D11 object is
        // accessed from the encoder thread. This is intentionally separate from the
        // legacy GPU readback method below.
        public void WriteVideoFrame(byte[] bgra, int srcWidth, int srcHeight, long timestampHns = -1)
        {
            if (!_started || _writer == null || bgra == null || bgra.Length == 0)
                return;

            lock (_lock)
            {
                if (!_started || _writer == null)
                    return;

                if (srcWidth <= 0 || srcHeight <= 0)
                    throw new ArgumentException("잘못된 비디오 프레임 크기입니다.");

                int srcRowBytes = checked(srcWidth * 4);
                int requiredSrcBytes = checked(srcRowBytes * srcHeight);
                if (bgra.Length < requiredSrcBytes)
                    throw new ArgumentException("BGRA 프레임 버퍼 크기가 실제 프레임보다 작습니다.");

                int ySize = checked(Width * Height);
                int uvSize = checked(ySize / 2);
                int nv12Size = checked(ySize + uvSize);
                byte[] nv12 = BgraToNv12(bgra, srcWidth, srcHeight, Width, Height);

                if (nv12.Length != nv12Size)
                    throw new InvalidOperationException("NV12 변환 결과 크기가 잘못되었습니다.");

                ThrowIfFailed(MFCreateMemoryBuffer(nv12Size, out var buffer), "MFCreateMemoryBuffer(video)");
                try
                {
                    ThrowIfFailed(buffer.Lock(out IntPtr dst, out _, out _), "Lock(video buffer)");
                    try
                    {
                        Marshal.Copy(nv12, 0, dst, nv12.Length);
                        ThrowIfFailed(buffer.SetCurrentLength(nv12.Length), "SetCurrentLength(video buffer)");
                    }
                    finally
                    {
                        try { buffer.Unlock(); } catch { }
                    }

                    ThrowIfFailed(MFCreateSample(out var sample), "MFCreateSample(video)");
                    try
                    {
                        ThrowIfFailed(sample.AddBuffer(buffer), "AddBuffer(video)");
                        // Prefer capture-time timestamp so A/V share one wall clock.
                        // Keep monotonic per stream (MF requirement).
                        long vTime = timestampHns >= 0 ? timestampHns : _videoTimestamp;
                        if (vTime < _videoTimestamp)
                            vTime = _videoTimestamp;
                        ThrowIfFailed(sample.SetSampleTime(vTime), "SetSampleTime(video)");
                        ThrowIfFailed(sample.SetSampleDuration(_videoFrameDuration), "SetSampleDuration(video)");
                        ThrowIfFailed(_writer.WriteSample(_videoStreamIndex, sample), "WriteSample(video)");
                        _videoTimestamp = vTime + _videoFrameDuration;
                    }
                    finally
                    {
                        try { ReleaseCom(sample); } catch { }
                    }
                }
                finally
                {
                    try { ReleaseCom(buffer); } catch { }
                }
            }
        }

        /// <summary>
        /// Convert BGRA to NV12 at a fixed encoder size.
        /// Source is scaled to FIT the destination while preserving aspect ratio
        /// (letterbox / pillarbox with black bars). Stretch was avoided because
        /// window resizes would otherwise distort the picture.
        /// </summary>
        private static byte[] BgraToNv12(byte[] bgra, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
        {
            int dstYSize = checked(dstWidth * dstHeight);
            byte[] nv12 = new byte[checked(dstYSize + dstYSize / 2)];
            // Black in limited-range Y = 16, neutral chroma = 128
            for (int i = 0; i < dstYSize; i++)
                nv12[i] = 16;
            for (int i = dstYSize; i < nv12.Length; i++)
                nv12[i] = 128;

            if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
                return nv12;

            int srcStride = checked(srcWidth * 4);
            int uvBase = dstYSize;

            // Fit (contain): scale uniformly so the whole source is visible.
            long scaleNum = Math.Min(
                (long)dstWidth * srcHeight,
                (long)dstHeight * srcWidth);
            // scale = min(dstW/srcW, dstH/srcH) applied in integer form:
            // outW = srcW * min(dstW/srcW, dstH/srcH) = min(dstW, srcW*dstH/srcH)
            int outW = (int)Math.Min(dstWidth, (long)srcWidth * dstHeight / srcHeight);
            int outH = (int)Math.Min(dstHeight, (long)srcHeight * dstWidth / srcWidth);
            if (outW < 1) outW = 1;
            if (outH < 1) outH = 1;
            // Keep even for NV12 chroma alignment.
            outW -= outW % 2;
            outH -= outH % 2;
            if (outW < 2) outW = 2;
            if (outH < 2) outH = 2;
            if (outW > dstWidth) outW = dstWidth - (dstWidth % 2);
            if (outH > dstHeight) outH = dstHeight - (dstHeight % 2);

            int offX = (dstWidth - outW) / 2;
            int offY = (dstHeight - outH) / 2;
            offX -= offX % 2;
            offY -= offY % 2;

            // Fast path: same size and no letterbox needed.
            if (srcWidth == dstWidth && srcHeight == dstHeight && offX == 0 && offY == 0)
            {
                for (int y = 0; y < dstHeight; y++)
                {
                    int srcRow = y * srcStride;
                    int dstRow = y * dstWidth;
                    for (int x = 0; x < dstWidth; x++)
                    {
                        int p = srcRow + x * 4;
                        int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                        nv12[dstRow + x] = ClampByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                    }
                }
                for (int y = 0; y < dstHeight; y += 2)
                {
                    int uvRow = (y / 2) * dstWidth;
                    int srcRow0 = y * srcStride;
                    int srcRow1 = Math.Min(y + 1, dstHeight - 1) * srcStride;
                    for (int x = 0; x < dstWidth; x += 2)
                    {
                        int x1 = Math.Min(x + 1, dstWidth - 1);
                        int p00 = srcRow0 + x * 4, p01 = srcRow0 + x1 * 4;
                        int p10 = srcRow1 + x * 4, p11 = srcRow1 + x1 * 4;
                        int b = (bgra[p00] + bgra[p01] + bgra[p10] + bgra[p11]) / 4;
                        int g = (bgra[p00 + 1] + bgra[p01 + 1] + bgra[p10 + 1] + bgra[p11 + 1]) / 4;
                        int r = (bgra[p00 + 2] + bgra[p01 + 2] + bgra[p10 + 2] + bgra[p11 + 2]) / 4;
                        int uv = uvBase + uvRow + x;
                        nv12[uv] = ClampByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                        nv12[uv + 1] = ClampByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                    }
                }
                return nv12;
            }

            // Y plane — scaled into the letterboxed rectangle.
            for (int y = 0; y < outH; y++)
            {
                int srcY = (int)((long)y * srcHeight / outH);
                if (srcY >= srcHeight) srcY = srcHeight - 1;
                int srcRow = srcY * srcStride;
                int dstRow = (y + offY) * dstWidth + offX;
                for (int x = 0; x < outW; x++)
                {
                    int srcX = (int)((long)x * srcWidth / outW);
                    if (srcX >= srcWidth) srcX = srcWidth - 1;
                    int p = srcRow + srcX * 4;
                    int b = bgra[p], g = bgra[p + 1], r = bgra[p + 2];
                    nv12[dstRow + x] = ClampByte(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                }
            }

            // UV plane — same rectangle, 2x2 subsampled.
            for (int y = 0; y < outH; y += 2)
            {
                int srcY0 = (int)((long)y * srcHeight / outH);
                int srcY1 = (int)((long)Math.Min(y + 1, outH - 1) * srcHeight / outH);
                if (srcY0 >= srcHeight) srcY0 = srcHeight - 1;
                if (srcY1 >= srcHeight) srcY1 = srcHeight - 1;
                int srcRow0 = srcY0 * srcStride;
                int srcRow1 = srcY1 * srcStride;
                int uvRow = ((y + offY) / 2) * dstWidth;

                for (int x = 0; x < outW; x += 2)
                {
                    int srcX0 = (int)((long)x * srcWidth / outW);
                    int srcX1 = (int)((long)Math.Min(x + 1, outW - 1) * srcWidth / outW);
                    if (srcX0 >= srcWidth) srcX0 = srcWidth - 1;
                    if (srcX1 >= srcWidth) srcX1 = srcWidth - 1;

                    int p00 = srcRow0 + srcX0 * 4, p01 = srcRow0 + srcX1 * 4;
                    int p10 = srcRow1 + srcX0 * 4, p11 = srcRow1 + srcX1 * 4;
                    int b = (bgra[p00] + bgra[p01] + bgra[p10] + bgra[p11]) / 4;
                    int g = (bgra[p00 + 1] + bgra[p01 + 1] + bgra[p10 + 1] + bgra[p11 + 1]) / 4;
                    int r = (bgra[p00 + 2] + bgra[p01 + 2] + bgra[p10 + 2] + bgra[p11 + 2]) / 4;

                    int uv = uvBase + uvRow + (x + offX);
                    nv12[uv] = ClampByte(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    nv12[uv + 1] = ClampByte(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }

            return nv12;
        }

        private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

        public void WriteVideoFrame(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D srcTexture, int srcWidth, int srcHeight)
        {
            if (!_started || _writer == null) return;

            lock (_lock)
            {
                // Reuse the CPU-readable staging texture instead of allocating a new
                // D3D11 resource for every frame. The old implementation could create
                // hundreds of resources per second at high resolutions, which made the
                // WGC callback stall and eventually killed the application.
                if (_stagingTexture == null || _stagingWidth != srcWidth || _stagingHeight != srcHeight)
                {
                    _stagingTexture?.Dispose();
                    var stagingDesc = new Texture2DDescription
                    {
                        Width = (uint)srcWidth,
                        Height = (uint)srcHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };
                    _stagingTexture = device.CreateTexture2D(stagingDesc);
                    _stagingWidth = srcWidth;
                    _stagingHeight = srcHeight;
                }

                // Both resources must belong to the same D3D11 device. RecordingController
                // now passes the capture session's device/context, so this CopyResource is
                // a legal GPU copy instead of a cross-device operation.
                context.CopyResource(_stagingTexture, srcTexture);

                Vortice.Direct3D11.MappedSubresource mapped;
                try
                {
                    mapped = context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                }
                catch (Exception ex)
                {
                    throw new COMException($"D3D11 staging texture Map 실패: {ex.Message}", unchecked((int)0x887A0005));
                }

                int byteSize = Width * Height * 4;
                ThrowIfFailed(MFCreateMemoryBuffer(byteSize, out var buffer), "MFCreateMemoryBuffer(video)");
                ThrowIfFailed(buffer.Lock(out IntPtr dst, out _, out _), "Lock(video buffer)");

                try
                {
                    unsafe
                    {
                        byte* srcPtr = (byte*)mapped.DataPointer;
                        byte* dstPtr = (byte*)dst;
                        int copyWidthBytes = Width * 4;
                        int rows = Math.Min(Height, srcHeight);
                        for (int y = 0; y < rows; y++)
                        {
                            Buffer.MemoryCopy(srcPtr + y * mapped.RowPitch, dstPtr + y * copyWidthBytes, copyWidthBytes, copyWidthBytes);
                        }
                    }
                }
                finally
                {
                    try { buffer.SetCurrentLength(byteSize); } finally { try { buffer.Unlock(); } finally { context.Unmap(_stagingTexture, 0); } }
                }

                ThrowIfFailed(MFCreateSample(out var sample), "MFCreateSample(video)");
                sample.AddBuffer(buffer);
                sample.SetSampleTime(_videoTimestamp);
                sample.SetSampleDuration(_videoFrameDuration);
                _videoTimestamp += _videoFrameDuration;

                ThrowIfFailed(_writer.WriteSample(_videoStreamIndex, sample), "WriteSample(video)");

                ReleaseCom(buffer);
                ReleaseCom(sample);
            }
        }

        public void WriteAudioSamples(byte[] pcmData, long timestampHns = -1)
        {
            if (!_started || _writer == null || !HasAudio || pcmData == null || pcmData.Length == 0)
                return;

            lock (_lock)
            {
                if (!_started || _writer == null || !HasAudio)
                    return;

                const int bytesPerFrame = 4; // 16-bit stereo PCM
                long sampleFrames = pcmData.Length / bytesPerFrame;
                if (sampleFrames <= 0)
                    return;

                long durationHns = sampleFrames * 10_000_000L / 48_000L;
                if (durationHns <= 0)
                    durationHns = 1;

                IMFMediaBuffer? buffer = null;
                IMFSample? sample = null;
                try
                {
                    ThrowIfFailed(MFCreateMemoryBuffer(pcmData.Length, out buffer), "MFCreateMemoryBuffer(audio)");
                    ThrowIfFailed(buffer.Lock(out IntPtr dst, out _, out _), "Lock(audio buffer)");
                    try
                    {
                        Marshal.Copy(pcmData, 0, dst, pcmData.Length);
                        ThrowIfFailed(buffer.SetCurrentLength(pcmData.Length), "SetCurrentLength(audio buffer)");
                    }
                    finally
                    {
                        try { buffer.Unlock(); } catch { }
                    }

                    ThrowIfFailed(MFCreateSample(out sample), "MFCreateSample(audio)");
                    ThrowIfFailed(sample.AddBuffer(buffer), "AddBuffer(audio)");
                    long aTime = timestampHns >= 0 ? timestampHns : _audioTimestamp;
                    if (aTime < _audioTimestamp)
                        aTime = _audioTimestamp;
                    ThrowIfFailed(sample.SetSampleTime(aTime), "SetSampleTime(audio)");
                    ThrowIfFailed(sample.SetSampleDuration(durationHns), "SetSampleDuration(audio)");
                    ThrowIfFailed(_writer.WriteSample(_audioStreamIndex, sample), "WriteSample(audio)");
                    _audioTimestamp = aTime + durationHns;
                }
                finally
                {
                    try { if (sample != null) ReleaseCom(sample); } catch { }
                    try { if (buffer != null) ReleaseCom(buffer); } catch { }
                }
            }
        }

        public void Finish()
        {
            lock (_lock)
            {
                if (_started && _writer != null)
                {
                    try
                    {
                        // FinalizeWriting is what writes the MP4 indexes/moov
                        // metadata. It must be called exactly once after all
                        // audio/video samples have stopped.
                        ThrowIfFailed(_writer.FinalizeWriting(), "FinalizeWriting");
                    }
                    finally
                    {
                        _started = false;
                    }
                }
            }
        }

        public void Dispose()
        {
            Finish();
            _stagingTexture?.Dispose();
            _stagingTexture = null;

            if (_writer != null)
            {
                try { ReleaseCom(_writer); } catch { }
                _writer = null;
            }
            if (_mfStarted)
            {
                try { MFShutdown(); } catch { }
                _mfStarted = false;
            }
        }

        // ---- helpers ----
        private static void SetGuid(IMFMediaType type, Guid key, Guid value)
        {
            ThrowIfFailed(type.SetGUID(ref AsRef(key), ref value), $"SetGUID({key})");
        }

        private static void SetPacked64(IMFMediaType type, Guid key, uint high, uint low)
        {
            long packed = ((long)high << 32) | low;
            ThrowIfFailed(type.SetUINT64(ref AsRef(key), packed), $"SetUINT64({key})");
        }

        private static ref Guid AsRef(Guid g) => ref System.Runtime.CompilerServices.Unsafe.AsRef(in g);

        private static void ReleaseCom(object? value)
        {
            if (value == null) return;
            try
            {
                if (Marshal.IsComObject(value))
                    Marshal.FinalReleaseComObject(value);
            }
            catch
            {
                // Some WinRT/COM RCWs are already released by the runtime. Never
                // let cleanup turn a successfully finalized MP4 into an exception.
            }
        }

        private static void ThrowIfFailed(int hr, string context)
        {
            if (hr < 0)
                throw new COMException($"Media Foundation 호출 실패: {context} (HRESULT=0x{hr:X8})", hr);
        }
    }
}
