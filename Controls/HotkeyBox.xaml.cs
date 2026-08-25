using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace WinCapRecorder.Controls
{
    public partial class HotkeyBox : System.Windows.Controls.UserControl
    {
        public event EventHandler<(ModifierKeys mods, Key key)>? HotkeyChanged;
        public event EventHandler<bool>? ListeningChanged;

        private ModifierKeys _modifiers = ModifierKeys.None;
        private Key _key = Key.None;
        private bool _listening;
        private System.Windows.Window? _hostWindow;

        private static readonly System.Windows.Media.SolidColorBrush IdleBg =
            new(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        private static readonly System.Windows.Media.SolidColorBrush ListeningBg =
            new(Color.FromArgb(0x66, 0x4A, 0x9E, 0xFF));
        private static readonly System.Windows.Media.SolidColorBrush ListeningBorder =
            new(Color.FromArgb(0xFF, 0x4A, 0x9E, 0xFF));
        private static readonly System.Windows.Media.SolidColorBrush IdleBorder =
            new(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));

        public HotkeyBox()
        {
            InitializeComponent();
            IdleBg.Freeze();
            ListeningBg.Freeze();
            ListeningBorder.Freeze();
            IdleBorder.Freeze();
            Focusable = true;
            IsTabStop = true;
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            PreviewKeyDown += OnPreviewKeyDown;
            GotKeyboardFocus += (_, _) => { if (!_listening) EnterListeningMode(); };
            LostKeyboardFocus += (_, _) => { if (_listening) ExitListeningMode(notify: true); };
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshDisplay();
            _hostWindow = System.Windows.Window.GetWindow(this);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DetachWindowHook();
            _hostWindow = null;
        }

        public void SetValue(ModifierKeys mods, Key key)
        {
            _modifiers = mods;
            _key = key;
            if (!_listening)
                RefreshDisplay();
        }

        public (ModifierKeys mods, Key key) GetValue() => (_modifiers, _key);

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                Focusable = true;
                Keyboard.Focus(this);
                EnterListeningMode();
            });
        }

        private void EnterListeningMode()
        {
            if (_listening) return;
            _listening = true;
            DisplayText.Text = "키를 눌러주세요... (Esc=해제)";
            RootBorder.Background = ListeningBg;
            RootBorder.BorderBrush = ListeningBorder;
            AttachWindowHook();
            ListeningChanged?.Invoke(this, true);
        }

        private void ExitListeningMode(bool notify)
        {
            if (!_listening) return;
            _listening = false;
            DetachWindowHook();
            RootBorder.Background = IdleBg;
            RootBorder.BorderBrush = IdleBorder;
            RefreshDisplay();
            if (notify)
                ListeningChanged?.Invoke(this, false);
        }

        private void AttachWindowHook()
        {
            _hostWindow ??= System.Windows.Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
                _hostWindow.PreviewKeyDown += OnWindowPreviewKeyDown;
            }
        }

        private void DetachWindowHook()
        {
            if (_hostWindow != null)
                _hostWindow.PreviewKeyDown -= OnWindowPreviewKeyDown;
        }

        private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_listening)
                HandleKey(e);
        }

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_listening)
                HandleKey(e);
        }

        private void HandleKey(System.Windows.Input.KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin ||
                key == Key.System)
            {
                e.Handled = true;
                return;
            }

            if (key == Key.Escape)
            {
                _key = Key.None;
                _modifiers = ModifierKeys.None;
                e.Handled = true;
                HotkeyChanged?.Invoke(this, (_modifiers, _key));
                ExitListeningMode(notify: true);
                Keyboard.ClearFocus();
                return;
            }

            if (key == Key.Tab)
                return;

            _modifiers = Keyboard.Modifiers &
                (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows);
            _key = key;
            e.Handled = true;
            HotkeyChanged?.Invoke(this, (_modifiers, _key));
            ExitListeningMode(notify: true);
            Keyboard.ClearFocus();
        }

        private void RefreshDisplay()
        {
            if (_key == Key.None)
            {
                DisplayText.Text = "클릭 후 키 입력";
                return;
            }
            DisplayText.Text = FormatDisplay(_modifiers, _key);
        }

        private static string FormatKey(Key key)
        {
            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
                return name[1].ToString();
            return name;
        }

        private static string FormatDisplay(ModifierKeys modifiers, Key key)
        {
            string mods = "";
            if (modifiers.HasFlag(ModifierKeys.Control)) mods += "Ctrl+";
            if (modifiers.HasFlag(ModifierKeys.Alt)) mods += "Alt+";
            if (modifiers.HasFlag(ModifierKeys.Shift)) mods += "Shift+";
            if (modifiers.HasFlag(ModifierKeys.Windows)) mods += "Win+";
            return mods + FormatKey(key);
        }
    }
}
