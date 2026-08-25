using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinCapRecorder.Audio;
using WinCapRecorder.Capture;

namespace WinCapRecorder
{
    public enum RecordingState { Idle, Recording, Paused }

    public sealed class RecordingController : IDisposable
    {
        private WindowCaptureSession? _capture;
        private ProcessLoopbackCapture? _audioCapture;
        private Encode.Mp4Writer? _writer;

        private readonly object _queueLock = new();
        private readonly Queue<EncoderItem> _encoderQueue = new();
        private readonly AutoResetEvent _encoderSignal = new(false);
        private Thread? _encoderWorker;
        private volatile bool _workerRunning;
        private volatile bool _encoderFaulted;
        private const int MaxQueuedItems = 120;
        private int _stopInProgress;
        private ManualResetEventSlim? _writerReady;
        private Exception? _writerInitException;
        private bool _writerAudioEnabled;

        public RecordingState State { get; private set; } = RecordingState.Idle;
        public bool AudioEnabled { get; set; } = true;
        // Runtime mute during an active recording (does not remove the AAC track).
        private volatile bool _audioOutputEnabled = true;
        public string? CurrentOutputPath { get; private set; }
        public Stopwatch Elapsed { get; } = new();

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<Exception>? ErrorOccurred;

        private const int TargetFps = 30;
        private const long VideoBitrateBps = 20_000_000;

        private enum EncoderItemKind { Video, Audio }

        private sealed class EncoderItem
        {
            public EncoderItemKind Kind { get; }
            public byte[] Data { get; }
            public int Width { get; }
            public int Height { get; }
            /// <summary>Capture-time timeline in 100-ns units (shared A/V clock).</summary>
            public long TimestampHns { get; }

            private EncoderItem(EncoderItemKind kind, byte[] data, int width, int height, long timestampHns)
            {
                Kind = kind;
                Data = data;
                Width = width;
                Height = height;
                TimestampHns = timestampHns;
            }

            public static EncoderItem Video(byte[] data, int width, int height, long timestampHns) =>
                new(EncoderItemKind.Video, data, width, height, timestampHns);

            public static EncoderItem Audio(byte[] data, long timestampHns) =>
                new(EncoderItemKind.Audio, data, 0, 0, timestampHns);
        }

        public Task<bool> StartAsync(IntPtr targetHwnd, uint targetPid, string outputDirectory) =>
            Task.Run(() => Start(targetHwnd, targetPid, outputDirectory));

        public bool Start(IntPtr targetHwnd, uint targetPid, string outputDirectory)
        {
            if (State != RecordingState.Idle)
                return false;

            try
            {
                Directory.CreateDirectory(outputDirectory);
                CurrentOutputPath = Path.Combine(
                    outputDirectory,
                    $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                _encoderFaulted = false;
                _writerInitException = null;
                StatusChanged?.Invoke(this, "캡처 준비 중...");

                // Privilege mismatch is the #1 cause of white video + silent audio.
                // Windows deliberately blanks WGC frames and process-loopback audio
                // when the target runs at a higher integrity level than us.
                try
                {
                    bool targetElevated = Native.NativeMethods.IsProcessElevated(targetPid);
                    bool selfElevated = Native.NativeMethods.IsUserAnAdmin();
                    if (targetElevated && !selfElevated)
                    {
                        StatusChanged?.Invoke(this,
                            "⚠ 대상 창이 관리자 권한으로 실행 중입니다. 흰 화면/무음이 나오면 " +
                            "WinCapRecorder.exe를 우클릭 → '관리자 권한으로 실행' 후 다시 녹화하세요.");
                        try
                        {
                            File.AppendAllText(
                                Path.Combine(AppContext.BaseDirectory, "crash.log"),
                                $"[{DateTime.Now:O}] PRIVILEGE_MISMATCH targetPid={targetPid} " +
                                "targetElevated=true selfElevated=false");
                        }
                        catch { }
                    }
                }
                catch { }

                _capture = new WindowCaptureSession();
                if (!_capture.Start(targetHwnd))
                    throw new InvalidOperationException("이 창은 캡처할 수 없습니다. 다른 창을 선택해주세요.");

                // Always open process-loopback + AAC when the OS supports it, so the
                // user can toggle sound any number of times mid-recording.
                // Mute is implemented by writing silence (not stopping WASAPI).
                bool audioOk = false;
                _audioCapture = new ProcessLoopbackCapture();
                audioOk = _audioCapture.Start(targetPid);
                if (!audioOk)
                {
                    try { _audioCapture.Dispose(); } catch { }
                    _audioCapture = null;
                    StatusChanged?.Invoke(this, "소리 캡처를 사용할 수 없어 영상만 녹화합니다.");
                }

                _writerAudioEnabled = audioOk;
                _audioOutputEnabled = audioOk && AudioEnabled;
                _writerReady = new ManualResetEventSlim(false);
                StatusChanged?.Invoke(this, "인코더 준비 중...");

                _capture.FrameArrived += OnFrameArrived;
                _capture.FrameError += OnCaptureFrameError;
                _capture.CaptureWarning += OnCaptureWarning;
                if (_audioCapture != null)
                {
                    _audioCapture.DataAvailable += OnAudioData;
                    _audioCapture.AudioWarning += OnAudioWarning;
                }

                StartEncoderWorker();

                if (!_writerReady.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Media Foundation 인코더 초기화 시간이 초과되었습니다.");

                if (_writerInitException != null)
                    throw new InvalidOperationException(
                        "Media Foundation 인코더 초기화 실패",
                        _writerInitException);

                State = RecordingState.Recording;
                Elapsed.Restart();
                StatusChanged?.Invoke(this, "녹화 중");
                return true;
            }
            catch (Exception ex)
            {
                CleanupAfterFailure();
                try { ErrorOccurred?.Invoke(this, ex); } catch { }
                return false;
            }
        }

        private void StartEncoderWorker()
        {
            lock (_queueLock)
            {
                _encoderQueue.Clear();
                _workerRunning = true;
            }

            _encoderWorker = new Thread(EncoderWorkerLoop)
            {
                IsBackground = true,
                Name = "WinCapRecorder.MediaFoundationWriter"
            };
            _encoderWorker.Start();
        }

        private void EncoderWorkerLoop()
        {
            // IMPORTANT: the SinkWriter is created, used, finalized and released
            // on ONE thread. No other thread calls Media Foundation anymore.
            try
            {
                var capture = _capture;
                if (capture == null)
                    throw new InvalidOperationException("캡처 세션이 없습니다.");

                _writer = new Encode.Mp4Writer(
                    CurrentOutputPath!,
                    capture.Width,
                    capture.Height,
                    TargetFps,
                    _writerAudioEnabled,
                    VideoBitrateBps);
            }
            catch (Exception ex)
            {
                _writerInitException = ex;
                _encoderFaulted = true;
                lock (_queueLock) _workerRunning = false;
                try { _writerReady?.Set(); } catch { }
                LogBackgroundError("MEDIA_FOUNDATION_INIT", ex);
                return;
            }

            try { _writerReady?.Set(); } catch { }

            Exception? workerError = null;

            while (true)
            {
                EncoderItem? item = null;
                lock (_queueLock)
                {
                    if (_encoderQueue.Count > 0)
                        item = _encoderQueue.Dequeue();
                    else if (!_workerRunning)
                        break;
                }

                if (item == null)
                {
                    _encoderSignal.WaitOne(100);
                    continue;
                }

                try
                {
                    if (_writer == null)
                        throw new InvalidOperationException("Media Foundation writer가 없습니다.");

                    if (item.Kind == EncoderItemKind.Video)
                        _writer.WriteVideoFrame(item.Data, item.Width, item.Height, item.TimestampHns);
                    else
                        _writer.WriteAudioSamples(item.Data, item.TimestampHns);
                }
                catch (Exception ex)
                {
                    _encoderFaulted = true;
                    workerError = ex;
                    LogBackgroundError("MEDIA_FOUNDATION_ENCODER", ex);

                    // Do not let one bad sample race with Stop(). Stop producing new
                    // samples, then finish the writer on THIS SAME COM thread.
                    lock (_queueLock)
                        _encoderQueue.Clear();
                    _workerRunning = false;
                    try { ErrorOccurred?.Invoke(this, ex); } catch { }
                    break;
                }
            }

            // Finalize and release the writer on the same thread that created/used it.
            try
            {
                _writer?.Finish();
            }
            catch (Exception ex)
            {
                LogBackgroundError("FINALIZE", ex);
                if (workerError == null)
                {
                    _encoderFaulted = true;
                    try { ErrorOccurred?.Invoke(this, ex); } catch { }
                }
            }
            finally
            {
                try { _writer?.Dispose(); } catch (Exception ex) { LogBackgroundError("WRITER_DISPOSE", ex); }
                _writer = null;
            }
        }

        private void OnFrameArrived(object? sender, FrameArrivedEventArgs e)
        {
            if (State != RecordingState.Recording || _encoderFaulted || _stopInProgress != 0)
                return;

            lock (_queueLock)
            {
                if (!_workerRunning)
                    return;

                if (_encoderQueue.Count >= MaxQueuedItems)
                {
                    // Drop one old video frame rather than blocking WGC.
                    var tmp = new Queue<EncoderItem>(_encoderQueue.Count);
                    bool removed = false;
                    while (_encoderQueue.Count > 0)
                    {
                        var item = _encoderQueue.Dequeue();
                        if (!removed && item.Kind == EncoderItemKind.Video)
                        {
                            removed = true;
                            continue;
                        }
                        tmp.Enqueue(item);
                    }
                    while (tmp.Count > 0)
                        _encoderQueue.Enqueue(tmp.Dequeue());

                    if (!removed)
                        return;
                }

                _encoderQueue.Enqueue(EncoderItem.Video(e.Bgra, e.Width, e.Height, GetCaptureTimestampHns()));
            }

            _encoderSignal.Set();
        }

        private void OnCaptureFrameError(object? sender, Exception ex)
        {
            if (State == RecordingState.Recording)
                LogBackgroundError("CAPTURE_FRAME_SKIPPED", ex);
        }

        private void OnCaptureWarning(object? sender, string message)
        {
            // Non-fatal: recording keeps running, but the user is told in the
            // status bar why the output may look wrong instead of finding out
            // only after opening a blank video.
            StatusChanged?.Invoke(this, "⚠ " + message);
        }

        private void OnAudioWarning(object? sender, string message)
        {
            StatusChanged?.Invoke(this, "⚠ " + message);
        }

        private void OnAudioData(object? sender, byte[] data)
        {
            if (State != RecordingState.Recording || _encoderFaulted || data.Length == 0 || _stopInProgress != 0)
                return;
            // No audio track in this session.
            if (!_writerAudioEnabled)
                return;

            lock (_queueLock)
            {
                if (!_workerRunning)
                    return;

                if (_encoderQueue.Count >= MaxQueuedItems)
                {
                    var tmp = new Queue<EncoderItem>(_encoderQueue.Count);
                    bool removed = false;
                    while (_encoderQueue.Count > 0)
                    {
                        var item = _encoderQueue.Dequeue();
                        if (!removed && item.Kind == EncoderItemKind.Video)
                        {
                            removed = true;
                            continue;
                        }
                        tmp.Enqueue(item);
                    }
                    while (tmp.Count > 0)
                        _encoderQueue.Enqueue(tmp.Dequeue());

                    if (!removed && _encoderQueue.Count >= MaxQueuedItems)
                        return;
                }

                byte[] payload;
                if (_audioOutputEnabled)
                {
                    payload = new byte[data.Length];
                    Buffer.BlockCopy(data, 0, payload, 0, data.Length);
                }
                else
                {
                    // Muted: write silence of the same duration so the AAC timeline
                    // stays continuous and toggle on/off is precise in the file.
                    payload = new byte[data.Length];
                }

                _encoderQueue.Enqueue(EncoderItem.Audio(payload, GetCaptureTimestampHns()));
            }

            _encoderSignal.Set();
        }

        private static void LogBackgroundError(string source, Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:O}] {source}: {ex}\r\n\r\n");
            }
            catch { }
        }

        /// <summary>
        /// Enable/disable writing captured audio into the current recording, immediately.
        /// Purges already-queued audio packets so the change is not delayed by the encoder queue.
        /// If this session was started without an audio track, enabling is a no-op.
        /// </summary>
        public void SetAudioCaptureEnabled(bool enabled)
        {
            AudioEnabled = enabled;

            // Preference for next Start when idle.
            if (State == RecordingState.Idle)
                return;

            if (!_writerAudioEnabled || _audioCapture == null)
            {
                _audioOutputEnabled = false;
                if (enabled)
                    StatusChanged?.Invoke(this, "이 세션은 소리 캡처를 사용할 수 없습니다.");
                return;
            }

            bool wasEnabled = _audioOutputEnabled;
            _audioOutputEnabled = enabled;

            // Do NOT pause WASAPI for mute — multiple pause/resume cycles were
            // unreliable. Mute = write silence; unmute = write real PCM.
            // When turning OFF, drop already-queued real audio so mute is instant.
            if (wasEnabled && !enabled)
                PurgeQueuedAudio();

            StatusChanged?.Invoke(this, enabled ? "소리 녹화 ON" : "소리 녹화 OFF");
        }

        private void PurgeQueuedAudio()
        {
            lock (_queueLock)
            {
                if (_encoderQueue.Count == 0) return;
                var kept = new Queue<EncoderItem>(_encoderQueue.Count);
                while (_encoderQueue.Count > 0)
                {
                    var item = _encoderQueue.Dequeue();
                    if (item.Kind != EncoderItemKind.Audio)
                        kept.Enqueue(item);
                }
                while (kept.Count > 0)
                    _encoderQueue.Enqueue(kept.Dequeue());
            }
        }

        /// <summary>
        /// Shared recording clock in Media Foundation units (100 ns).
        /// Uses Elapsed so pause freezes both video and audio timelines together.
        /// </summary>
        private long GetCaptureTimestampHns()
        {
            // TimeSpan.Ticks is already 100-nanosecond units.
            long t = Elapsed.Elapsed.Ticks;
            return t < 0 ? 0 : t;
        }

        public void Pause()
        {
            if (State != RecordingState.Recording) return;
            State = RecordingState.Paused;
            _audioCapture?.SetPaused(true);
            Elapsed.Stop();
            StatusChanged?.Invoke(this, "일시정지");
        }

        public void Resume()
        {
            if (State != RecordingState.Paused) return;
            State = RecordingState.Recording;
            // Resume WASAPI capture; mute is handled by writing silence in OnAudioData.
            _audioCapture?.SetPaused(false);
            Elapsed.Start();
            StatusChanged?.Invoke(this, "녹화 재개");
        }

        public void TogglePause()
        {
            if (State == RecordingState.Recording) Pause();
            else if (State == RecordingState.Paused) Resume();
        }

        public string? Stop()
        {
            if (Interlocked.Exchange(ref _stopInProgress, 1) != 0)
                return CurrentOutputPath;

            try
            {
                if (State == RecordingState.Idle && _writer == null && _capture == null)
                    return null;

                // 1) Freeze the timeline immediately — no more samples accepted.
                State = RecordingState.Idle;
                Elapsed.Stop();
                _audioOutputEnabled = false;

                // 2) Detach producers so callbacks cannot enqueue anything new.
                if (_capture != null)
                {
                    _capture.FrameArrived -= OnFrameArrived;
                    _capture.FrameError -= OnCaptureFrameError;
                    _capture.CaptureWarning -= OnCaptureWarning;
                }
                if (_audioCapture != null)
                {
                    _audioCapture.DataAvailable -= OnAudioData;
                    _audioCapture.AudioWarning -= OnAudioWarning;
                }

                // 3) CRITICAL: drop the entire encoder queue NOW.
                //    Previously the worker kept draining residual audio packets for
                //    seconds after the last video frame, so the file had trailing sound
                //    after the picture already ended. User wants stop = hard cut.
                lock (_queueLock)
                {
                    _encoderQueue.Clear();
                    _workerRunning = false;
                }
                _encoderSignal.Set();

                // 4) Stop capture devices (may block briefly). Queue is already empty
                //    so the encoder thread will not write anything further.
                try { _audioCapture?.Stop(); } catch (Exception ex) { LogBackgroundError("AUDIO_STOP", ex); }
                try { _capture?.Stop(); } catch (Exception ex) { LogBackgroundError("CAPTURE_STOP", ex); }

                // 5) Wait for the encoder thread to FinalizeWriting + release.
                if (_encoderWorker != null && _encoderWorker != Thread.CurrentThread)
                    _encoderWorker.Join(15000);

                _encoderWorker = null;

                _writerReady?.Set();
                _writerReady?.Dispose();
                _writerReady = null;

                string? path = CurrentOutputPath;

                try { _capture?.Dispose(); } catch { }
                try { _audioCapture?.Dispose(); } catch { }
                _capture = null;
                _audioCapture = null;
                _writer = null;

                StatusChanged?.Invoke(this, "녹화 완료: " + path);
                return path;
            }
            catch (Exception ex)
            {
                LogBackgroundError("STOP", ex);
                try { ErrorOccurred?.Invoke(this, ex); } catch { }
                return CurrentOutputPath;
            }
            finally
            {
                Volatile.Write(ref _stopInProgress, 0);
            }
        }

        private void CleanupAfterFailure()
        {
            State = RecordingState.Idle;

            try
            {
                if (_capture != null)
                {
                    _capture.FrameArrived -= OnFrameArrived;
                    _capture.FrameError -= OnCaptureFrameError;
                    _capture.CaptureWarning -= OnCaptureWarning;
                }
            }
            catch { }
            try
            {
                if (_audioCapture != null)
                {
                    _audioCapture.DataAvailable -= OnAudioData;
                    _audioCapture.AudioWarning -= OnAudioWarning;
                }
            }
            catch { }

            try { _capture?.Stop(); } catch { }
            try { _audioCapture?.Stop(); } catch { }

            lock (_queueLock)
            {
                _encoderQueue.Clear();
                _workerRunning = false;
            }
            _encoderSignal.Set();
            try { _encoderWorker?.Join(15000); } catch { }
            _encoderWorker = null;

            try { _writerReady?.Set(); } catch { }
            _writerReady?.Dispose();
            _writerReady = null;

            try { _capture?.Dispose(); } catch { }
            try { _audioCapture?.Dispose(); } catch { }
            _capture = null;
            _audioCapture = null;
            _writer = null;
        }

        public void Dispose()
        {
            if (State != RecordingState.Idle || _writer != null || _capture != null)
                Stop();

            _encoderSignal.Dispose();
        }
    }
}
