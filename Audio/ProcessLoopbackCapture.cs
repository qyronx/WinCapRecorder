using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WinCapRecorder.Audio
{
    /// <summary>
    /// 특정 프로세스(및 그 자식 프로세스)에서 나오는 소리만 캡처.
    /// Windows 10 버전 2004(빌드 19041) 이상 필요 (Process Loopback API).
    ///
    /// Process-loopback virtual device does not expose a mix format, so we
    /// request IEEE float 48 kHz stereo (the format WASAPI process-loopback
    /// accepts most reliably) and convert to 16-bit PCM for the AAC encoder.
    /// </summary>
    public class ProcessLoopbackCapture : IDisposable
    {
        private AudioClient? _audioClient;
        private AudioCaptureClient? _captureClient;
        private WaveFormat? _waveFormat;
        private Thread? _thread;
        private volatile bool _running;
        private volatile bool _paused;
        private EventWaitHandle? _sampleEvent;
        private bool _isFloat;

        public WaveFormat? Format => _waveFormat;
        public event EventHandler<byte[]>? DataAvailable;

        public event EventHandler<string>? AudioWarning;
        private int _packetsSeen;
        private int _silentPackets;
        private int _nonzeroPackets;
        private bool _audioWarningReported;

        public bool Start(uint processId)
        {
            try
            {
                // COM must be initialized on the activation path.
                try { System.Runtime.InteropServices.Marshal.SetComObjectData(new object(), 0, null); } catch { }

                _audioClient = Task.Run(() =>
                    AudioClient.ActivateProcessLoopbackAsync(
                        processId,
                        ProcessLoopbackMode.IncludeTargetProcessTree))
                    .GetAwaiter().GetResult();

                // Prefer IEEE float @ 48 kHz stereo — process loopback accepts this
                // most consistently across Windows 10/11 builds. Fall back to PCM.
                Exception? last = null;
                // Mp4Writer AAC path is fixed at 48 kHz — only request 48 kHz formats.
                foreach (var candidate in new WaveFormat[]
                {
                    WaveFormat.CreateIeeeFloatWaveFormat(48000, 2),
                    new WaveFormat(48000, 16, 2),
                })
                {
                    try
                    {
                        _audioClient.Initialize(
                            AudioClientShareMode.Shared,
                            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
                            200000, // 20 ms
                            0,
                            candidate,
                            Guid.Empty);

                        _waveFormat = candidate;
                        _isFloat = candidate.Encoding == WaveFormatEncoding.IeeeFloat;
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }

                if (_waveFormat == null)
                {
                    throw last ?? new InvalidOperationException("프로세스 루프백 오디오 포맷 초기화 실패");
                }

                _sampleEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                _audioClient.SetEventHandle(_sampleEvent.SafeWaitHandle.DangerousGetHandle());

                _captureClient = _audioClient.AudioCaptureClient;
                _audioClient.Start();

                _running = true;
                _paused = false;
                _thread = new Thread(CaptureLoop)
                {
                    IsBackground = true,
                    Name = "ProcessLoopbackCapture"
                };
                _thread.Start();

                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                        $"[{DateTime.Now:O}] AUDIO_START: {ex}\r\n\r\n");
                }
                catch { }
                try { Dispose(); } catch { }
                return false;
            }
        }

        public void SetPaused(bool paused) => _paused = paused;

        private void CaptureLoop()
        {
            while (_running)
            {
                _sampleEvent?.WaitOne(200);
                if (!_running) break;
                if (_captureClient == null || _waveFormat == null) continue;

                try
                {
                    int packetSize = _captureClient.GetNextPacketSize();
                    while (packetSize > 0)
                    {
                        IntPtr buffer = _captureClient.GetBuffer(
                            out int numFrames,
                            out AudioClientBufferFlags flags);

                        int bytesPerFrame = _waveFormat.BlockAlign;
                        int byteCount = numFrames * bytesPerFrame;
                        bool isSilentFlag = (flags & AudioClientBufferFlags.Silent) != 0;

                        if (!_paused && byteCount > 0 && numFrames > 0)
                        {
                            byte[] pcm16;
                            if (isSilentFlag)
                            {
                                // Explicit silence flag → emit zeroed PCM16 of matching duration.
                                pcm16 = new byte[numFrames * 4];
                            }
                            else if (_isFloat)
                            {
                                pcm16 = FloatToPcm16(buffer, numFrames);
                            }
                            else
                            {
                                pcm16 = new byte[byteCount];
                                Marshal.Copy(buffer, pcm16, 0, byteCount);
                            }

                            if (!IsBufferSilent(pcm16))
                                _nonzeroPackets++;

                            DataAvailable?.Invoke(this, pcm16);
                        }

                        CheckAudioLevel(isSilentFlag);

                        _captureClient.ReleaseBuffer(numFrames);
                        packetSize = _captureClient.GetNextPacketSize();
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                            $"[{DateTime.Now:O}] AUDIO_CAPTURE: {ex}\r\n\r\n");
                    }
                    catch { }
                    break;
                }
            }
        }

        private static byte[] FloatToPcm16(IntPtr floatBuffer, int numFrames)
        {
            // IEEE float stereo → 16-bit PCM stereo
            int samples = numFrames * 2;
            byte[] pcm = new byte[samples * 2];
            unsafe
            {
                float* src = (float*)floatBuffer;
                fixed (byte* dstBytes = pcm)
                {
                    short* dst = (short*)dstBytes;
                    for (int i = 0; i < samples; i++)
                    {
                        float s = src[i];
                        if (s > 1f) s = 1f;
                        else if (s < -1f) s = -1f;
                        dst[i] = (short)(s * 32767f);
                    }
                }
            }
            return pcm;
        }

        private static bool IsBufferSilent(byte[] pcm16)
        {
            // Treat near-zero as silent so we don't false-positive on dither.
            int limit = Math.Min(pcm16.Length, 4096);
            for (int i = 0; i + 1 < limit; i += 2)
            {
                short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
                if (s > 8 || s < -8)
                    return false;
            }
            return true;
        }

        private void CheckAudioLevel(bool isSilentFlag)
        {
            if (_audioWarningReported) return;

            _packetsSeen++;
            if (isSilentFlag) _silentPackets++;

            // ~3 seconds of packets
            if (_packetsSeen < 150) return;

            _audioWarningReported = true;
            if (_nonzeroPackets > 0) return;

            const string message =
                "선택한 창에서 무음 데이터만 수신되고 있습니다. 대상 프로그램에서 소리가 실제로 재생 중인지 확인해주세요. " +
                "대상 프로그램이 관리자 권한으로 실행 중이라면 이 프로그램도 관리자 권한으로 다시 실행해보세요.";
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:O}] AUDIO_SILENT_WARNING packets={_packetsSeen} silentFlags={_silentPackets}: {message}\r\n\r\n");
            }
            catch { }
            try { AudioWarning?.Invoke(this, message); } catch { }
        }

        public void Stop()
        {
            _running = false;
            try { _audioClient?.Stop(); } catch { }
            try { _sampleEvent?.Set(); } catch { }
            if (_thread != null && _thread != Thread.CurrentThread)
            {
                try { _thread.Join(5000); } catch { }
            }
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
            _captureClient = null;
            try { _audioClient?.Dispose(); } catch { }
            _audioClient = null;
            try { _sampleEvent?.Dispose(); } catch { }
            _sampleEvent = null;
        }
    }
}
