using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WinCapRecorder.Native
{
    public class HotkeyManager : IDisposable
    {
        private HwndSource? _source;
        private IntPtr _handle;
        private int _nextId = 1;
        private readonly Dictionary<int, string> _idToAction = new();
        private readonly Dictionary<string, int> _actionToId = new();
        // Source of truth for what should be registered with the OS.
        private readonly Dictionary<string, (ModifierKeys mods, Key key)> _bindings = new();
        private bool _suspended;

        public event EventHandler<string>? HotkeyPressed;

        public HotkeyManager(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _handle = helper.EnsureHandle();
            _source = HwndSource.FromHwnd(_handle);
            if (_source == null)
                throw new InvalidOperationException("HwndSource를 만들지 못했습니다.");
            _source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && !_suspended)
            {
                int id = wParam.ToInt32();
                if (_idToAction.TryGetValue(id, out var action))
                {
                    try { HotkeyPressed?.Invoke(this, action); }
                    catch { }
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Unregister(string actionKey)
        {
            if (_actionToId.TryGetValue(actionKey, out int id))
            {
                NativeMethods.UnregisterHotKey(_handle, id);
                _actionToId.Remove(actionKey);
                _idToAction.Remove(id);
            }
            _bindings.Remove(actionKey);
        }

        public void UnregisterAll()
        {
            foreach (var id in new List<int>(_idToAction.Keys))
                NativeMethods.UnregisterHotKey(_handle, id);
            _idToAction.Clear();
            _actionToId.Clear();
            _bindings.Clear();
        }

        public bool Register(string actionKey, ModifierKeys modifiers, Key key)
        {
            // Drop any previous OS registration for this action.
            if (_actionToId.TryGetValue(actionKey, out int oldId))
            {
                NativeMethods.UnregisterHotKey(_handle, oldId);
                _actionToId.Remove(actionKey);
                _idToAction.Remove(oldId);
            }

            if (key == Key.None)
            {
                _bindings.Remove(actionKey);
                return true;
            }

            _bindings[actionKey] = (modifiers, key);

            if (_suspended)
                return true; // OS register deferred until Resume()

            return RegisterWithOs(actionKey, modifiers, key);
        }

        private bool RegisterWithOs(string actionKey, ModifierKeys modifiers, Key key)
        {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0)
                return false;

            uint mod = NativeMethods.MOD_NOREPEAT;
            if (modifiers.HasFlag(ModifierKeys.Alt)) mod |= NativeMethods.MOD_ALT;
            if (modifiers.HasFlag(ModifierKeys.Control)) mod |= NativeMethods.MOD_CONTROL;
            if (modifiers.HasFlag(ModifierKeys.Shift)) mod |= NativeMethods.MOD_SHIFT;
            if (modifiers.HasFlag(ModifierKeys.Windows)) mod |= NativeMethods.MOD_WIN;

            // Try with MOD_NOREPEAT first, then without (older behavior / some drivers).
            int id = _nextId++;
            bool ok = NativeMethods.RegisterHotKey(_handle, id, mod, (uint)vk);
            if (!ok)
            {
                uint mod2 = mod & ~NativeMethods.MOD_NOREPEAT;
                if (mod2 != mod)
                {
                    id = _nextId++;
                    ok = NativeMethods.RegisterHotKey(_handle, id, mod2, (uint)vk);
                }
            }

            if (ok)
            {
                _idToAction[id] = actionKey;
                _actionToId[actionKey] = id;
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:O}] HOTKEY_REGISTER_FAIL action={actionKey} mods={modifiers} key={key} vk=0x{vk:X} err={err}\r\n");
            }
            catch { }
            return false;
        }

        public void Suspend()
        {
            if (_suspended) return;
            _suspended = true;
            foreach (var id in new List<int>(_idToAction.Keys))
                NativeMethods.UnregisterHotKey(_handle, id);
            // Keep _idToAction / _actionToId / _bindings so Resume knows what to restore.
        }

        public void Resume()
        {
            if (!_suspended) return;
            _suspended = false;

            // Clear id maps then re-bind everything currently in _bindings.
            foreach (var id in new List<int>(_idToAction.Keys))
                NativeMethods.UnregisterHotKey(_handle, id);
            _idToAction.Clear();
            _actionToId.Clear();

            foreach (var kv in new Dictionary<string, (ModifierKeys mods, Key key)>(_bindings))
                RegisterWithOs(kv.Key, kv.Value.mods, kv.Value.key);
        }

        public void Dispose()
        {
            try { UnregisterAll(); } catch { }
            try { _source?.RemoveHook(WndProc); } catch { }
            _source = null;
        }
    }
}
