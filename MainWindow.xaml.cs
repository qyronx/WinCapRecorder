using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WinCapRecorder.Native;
using Forms = System.Windows.Forms;

namespace WinCapRecorder
{
    public partial class MainWindow : Window
    {
        private readonly RecordingController _controller = new();
        private HotkeyManager? _hotkeys;
        private AppSettings _settings = AppSettings.Load();
        private DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private Forms.NotifyIcon? _trayIcon;
        private CapturableWindow? _selectedWindow;
        private bool _closingToTray = true;
        private bool _startingRecording;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            _uiTimer.Tick += (s, e) => UpdateTimerDisplay();

            _controller.StatusChanged += (s, msg) =>
            {
                _ = Dispatcher.BeginInvoke(new Action(() => StatusText.Text = msg));
            };
            _controller.ErrorOccurred += (s, ex) =>
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    string detail = ex.Message;
                    var inner = ex.InnerException;
                    while (inner != null)
                    {
                        detail += "\n\n▶ " + inner.GetType().Name + ": " + inner.Message;
                        inner = inner.InnerException;
                    }

                    // An encoder/capture failure must not leave the recorder in a
                    // half-running state. Stop it before showing the error dialog.
                    try { StopRecording(); } catch { }
                    System.Windows.MessageBox.Show(detail, "녹화 오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }));
            };
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshWindowList();
            AudioCheckBox.IsChecked = _settings.AudioEnabled;
            _controller.AudioEnabled = _settings.AudioEnabled;

            HkStart.SetValue(_settings.StartHotkey.Modifiers, _settings.StartHotkey.Key);
            HkStop.SetValue(_settings.StopHotkey.Modifiers, _settings.StopHotkey.Key);
            HkPauseResume.SetValue(_settings.PauseResumeHotkey.Modifiers, _settings.PauseResumeHotkey.Key);
            HkToggleAudio.SetValue(_settings.ToggleAudioHotkey.Modifiers, _settings.ToggleAudioHotkey.Key);

            HkStart.HotkeyChanged += (s, v) => { _settings.StartHotkey.Modifiers = v.mods; _settings.StartHotkey.Key = v.key; ApplyHotkeys(); };
            HkStop.HotkeyChanged += (s, v) => { _settings.StopHotkey.Modifiers = v.mods; _settings.StopHotkey.Key = v.key; ApplyHotkeys(); };
            HkPauseResume.HotkeyChanged += (s, v) => { _settings.PauseResumeHotkey.Modifiers = v.mods; _settings.PauseResumeHotkey.Key = v.key; ApplyHotkeys(); };
            HkToggleAudio.HotkeyChanged += (s, v) => { _settings.ToggleAudioHotkey.Modifiers = v.mods; _settings.ToggleAudioHotkey.Key = v.key; ApplyHotkeys(); };

            // MUST use BeginInvoke — WndProc already runs on the UI thread.
            // Dispatcher.Invoke from the UI thread deadlocks and global hotkeys appear "broken".
            _hotkeys = new HotkeyManager(this);
            _hotkeys.HotkeyPressed += (s, action) =>
            {
                Dispatcher.BeginInvoke(new Action(() => HandleHotkeyAction(action)));
            };

            void WireListening(Controls.HotkeyBox box)
            {
                box.ListeningChanged += (_, listening) =>
                {
                    if (listening)
                        _hotkeys?.Suspend();
                    else
                        _hotkeys?.Resume();
                };
            }
            WireListening(HkStart);
            WireListening(HkStop);
            WireListening(HkPauseResume);
            WireListening(HkToggleAudio);

            ApplyHotkeys();

            OutputFolderText.Text = "저장 위치: " + _settings.OutputDirectory;

            SetupTrayIcon();
        }

        private void ApplyHotkeys()
        {
            if (_hotkeys == null) return;

            TryRegister("Start", _settings.StartHotkey);
            TryRegister("Stop", _settings.StopHotkey);
            TryRegister("PauseResume", _settings.PauseResumeHotkey);
            TryRegister("ToggleAudio", _settings.ToggleAudioHotkey);

            _settings.Save();
        }

        private void TryRegister(string action, HotkeyBinding binding)
        {
            if (_hotkeys == null) return;
            if (!binding.IsSet)
            {
                _hotkeys.Unregister(action);
                return;
            }
            bool ok = _hotkeys.Register(action, binding.Modifiers, binding.Key);
            if (!ok)
            {
                StatusText.Text = $"단축키 등록 실패 [{action}] {binding} — 다른 키로 바꿔주세요.";
            }
        }

        private void HandleHotkeyAction(string action)
        {
            try
            {
                switch (action)
                {
                    case "Start":
                        if (_controller.State != RecordingState.Idle)
                        {
                            StatusText.Text = "이미 녹화 중입니다.";
                            return;
                        }
                        if (WindowComboBox.SelectedItem == null)
                        {
                            RefreshWindowList();
                            if (WindowComboBox.SelectedItem == null)
                            {
                                StatusText.Text = "단축키: 녹화할 창을 먼저 선택하세요.";
                                return;
                            }
                        }
                        StatusText.Text = "단축키: 녹화 시작...";
                        _ = StartRecordingAsync();
                        break;
                    case "Stop":
                        if (_controller.State == RecordingState.Idle)
                        {
                            StatusText.Text = "단축키: 녹화 중이 아닙니다.";
                            return;
                        }
                        StatusText.Text = "단축키: 녹화 정지...";
                        StopRecording();
                        break;
                    case "PauseResume":
                        if (_controller.State == RecordingState.Idle)
                        {
                            StatusText.Text = "단축키: 녹화 중이 아닙니다.";
                            return;
                        }
                        _controller.TogglePause();
                        RefreshButtons();
                        StatusText.Text = _controller.State == RecordingState.Paused
                            ? "단축키: 일시정지"
                            : "단축키: 녹화 재개";
                        break;
                    case "ToggleAudio":
                    {
                        bool next = !(AudioCheckBox.IsChecked ?? false);
                        AudioCheckBox.IsChecked = next;
                        // Apply directly as well (Checked event also fires) so mid-recording
                        // toggle never depends on event ordering alone.
                        _controller.SetAudioCaptureEnabled(next);
                        StatusText.Text = next ? "단축키: 소리 녹화 ON" : "단축키: 소리 녹화 OFF";
                        break;
                    }
                    default:
                        StatusText.Text = "단축키: 알 수 없는 동작 " + action;
                        break;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "단축키 오류: " + ex.Message;
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                        $"[{DateTime.Now:O}] HOTKEY_ACTION {action}: {ex}\r\n");
                }
                catch { }
            }
        }

        private void RefreshWindowList()
        {
            var current = WindowComboBox.SelectedItem as CapturableWindow;
            var list = WindowEnumerator.GetCapturableWindows();
            WindowComboBox.ItemsSource = list;

            if (current != null)
            {
                foreach (var w in list)
                {
                    if (w.Handle == current.Handle) { WindowComboBox.SelectedItem = w; break; }
                }
            }
            if (WindowComboBox.SelectedItem == null && list.Count > 0)
                WindowComboBox.SelectedIndex = 0;
        }

        private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindowList();

        private void AudioCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool on = AudioCheckBox.IsChecked ?? false;
            _settings.AudioEnabled = on;
            // Live session: mute/unmute immediately. Preference also used on next Start.
            _controller.SetAudioCaptureEnabled(on);
            try { _settings.Save(); } catch { }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e) => await StartRecordingAsync();

        private async Task StartRecordingAsync()
        {
            if (_startingRecording || _controller.State != RecordingState.Idle) return;
            _startingRecording = true;
            try
            {
            var selected = WindowComboBox.SelectedItem as CapturableWindow;
            if (selected == null)
            {
                System.Windows.MessageBox.Show("녹화할 창을 선택해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!NativeMethods.IsWindow(selected.Handle))
            {
                System.Windows.MessageBox.Show("선택한 창이 이미 닫혔습니다. 목록을 새로고침 해주세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshWindowList();
                return;
            }

            _selectedWindow = selected;
            StatusText.Text = "녹화 준비 중...";
            StartButton.IsEnabled = false;
            bool ok = await _controller.StartAsync(selected.Handle, (uint)selected.ProcessId, _settings.OutputDirectory);
                if (ok)
                {
                    RefreshButtons();
                    WindowComboBox.IsEnabled = false;
                    _uiTimer.Start();
                    UpdateTrayIcon(true);
                }
                else
                {
                    _uiTimer.Stop();
                    WindowComboBox.IsEnabled = true;
                    RefreshButtons();
                }
            }
            finally
            {
                _startingRecording = false;
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _controller.TogglePause();
            RefreshButtons();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopRecording();

        private void StopRecording()
        {
            string? path = _controller.Stop();
            _uiTimer.Stop();
            WindowComboBox.IsEnabled = true;
            RefreshButtons();
            UpdateTimerDisplay();
            UpdateTrayIcon(false);

            if (!string.IsNullOrEmpty(path))
            {
                StatusText.Text = "저장됨: " + System.IO.Path.GetFileName(path);
            }
        }

        private void RefreshButtons()
        {
            var state = _controller.State;
            StartButton.IsEnabled = state == RecordingState.Idle;
            StopButton.IsEnabled = state != RecordingState.Idle;
            PauseButton.IsEnabled = state != RecordingState.Idle;
            PauseButton.Content = state == RecordingState.Paused ? "▶ 이어서 녹화" : "❚❚ 일시정지";

            StatusText.Text = state switch
            {
                RecordingState.Recording => "녹화 중",
                RecordingState.Paused => "일시정지됨",
                _ => StatusText.Text
            };
        }

        private void UpdateTimerDisplay()
        {
            var t = _controller.Elapsed.Elapsed;
            TimerText.Text = $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(_settings.OutputDirectory);
            Process.Start(new ProcessStartInfo { FileName = _settings.OutputDirectory, UseShellExecute = true });
        }

        private void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "녹화 영상을 저장할 폴더를 선택하세요.",
                UseDescriptionForTitle = true,
                SelectedPath = System.IO.Directory.Exists(_settings.OutputDirectory) ? _settings.OutputDirectory : "",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                _settings.OutputDirectory = dialog.SelectedPath;
                _settings.Save();
                OutputFolderText.Text = "저장 위치: " + _settings.OutputDirectory;
            }
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "WinCapRecorder"
            };
            _trayIcon.DoubleClick += (s, e) => { Show(); WindowState = WindowState.Normal; Activate(); };

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("열기", null, (s, e) => { Show(); WindowState = WindowState.Normal; Activate(); });
            menu.Items.Add("종료", null, (s, e) => { _closingToTray = false; Close(); });
            _trayIcon.ContextMenuStrip = menu;
        }

        private void UpdateTrayIcon(bool recording)
        {
            if (_trayIcon != null)
                _trayIcon.Text = recording ? "WinCapRecorder - 녹화 중" : "WinCapRecorder";
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                Hide();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closingToTray && _controller.State != RecordingState.Idle)
            {
                // 녹화 중이면 트레이로 숨김 (실수로 녹화 중단 방지)
                e.Cancel = true;
                Hide();
                return;
            }

            if (_controller.State != RecordingState.Idle)
                _controller.Stop();

            _hotkeys?.Dispose();
            _trayIcon?.Dispose();
        }
    }
}
