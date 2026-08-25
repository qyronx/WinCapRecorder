using System;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace WinCapRecorder
{
    public class HotkeyBinding
    {
        public ModifierKeys Modifiers { get; set; }
        public Key Key { get; set; }

        public bool IsSet => Key != Key.None;

        public override string ToString()
        {
            if (!IsSet) return "(설정 안 됨)";
            string mods = "";
            if (Modifiers.HasFlag(ModifierKeys.Control)) mods += "Ctrl+";
            if (Modifiers.HasFlag(ModifierKeys.Alt)) mods += "Alt+";
            if (Modifiers.HasFlag(ModifierKeys.Shift)) mods += "Shift+";
            if (Modifiers.HasFlag(ModifierKeys.Windows)) mods += "Win+";
            return mods + Key;
        }
    }

    public class AppSettings
    {
        public string OutputDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "WinCapRecorder");

        public HotkeyBinding StartHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.F9 };
        public HotkeyBinding StopHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.F10 };
        public HotkeyBinding PauseResumeHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.F11 };
        public HotkeyBinding ToggleAudioHotkey { get; set; } = new() { Modifiers = ModifierKeys.Control | ModifierKeys.Shift, Key = Key.F12 };

        public bool AudioEnabled { get; set; } = true;

        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinCapRecorder", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
