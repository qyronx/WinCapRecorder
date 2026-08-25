using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using WinCapRecorder.Native;

namespace WinCapRecorder.Native
{
    public class CapturableWindow
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public int ProcessId { get; set; }

        public override string ToString() => $"{Title}  [{ProcessName}]";
    }

    public static class WindowEnumerator
    {
        public static List<CapturableWindow> GetCapturableWindows()
        {
            var result = new List<CapturableWindow>();
            IntPtr shellWindow = NativeMethods.GetShellWindow();

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == shellWindow) return true;
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;
                if (NativeMethods.IsIconic(hWnd)) return true; // minimized

                int len = NativeMethods.GetWindowTextLengthW(hWnd);
                if (len == 0) return true;

                var sb = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                // skip tool windows (not real app windows)
                long exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                bool isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
                bool isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
                if (isToolWindow && !isAppWindow) return true;

                // must be its own root owner (skip child/owned popups)
                IntPtr root = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER);
                if (root != hWnd && root != IntPtr.Zero)
                {
                    // Some legitimate app windows still pass this; be lenient — only filter if clearly a tooltip/menu
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                string procName = "";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch { }

                if (procName.Equals("WinCapRecorder", StringComparison.OrdinalIgnoreCase))
                    return true; // don't list ourselves

                result.Add(new CapturableWindow
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessName = procName,
                    ProcessId = (int)pid
                });

                return true;
            }, IntPtr.Zero);

            return result;
        }
    }
}
