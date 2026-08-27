using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace WinCapRecorder.Native
{
    public class CapturableWindow
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public int ProcessId { get; set; }
        public bool IsMinimized { get; set; }

        /// <summary>Text shown in the ComboBox.</summary>
        public string DisplayName
        {
            get
            {
                string title = string.IsNullOrWhiteSpace(Title) ? "(제목 없음)" : Title;
                string proc = string.IsNullOrEmpty(ProcessName) ? "?" : ProcessName;
                string min = IsMinimized ? " [최소화]" : "";
                return $"{title}  [{proc}]{min}";
            }
        }

        public override string ToString() => DisplayName;
    }

    public static class WindowEnumerator
    {
        public static List<CapturableWindow> GetCapturableWindows()
        {
            var result = new List<CapturableWindow>();
            var seen = new HashSet<long>();
            IntPtr shellWindow = NativeMethods.GetShellWindow();
            int selfPid = Process.GetCurrentProcess().Id;

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == IntPtr.Zero || hWnd == shellWindow)
                    return true;

                // Skip pure child windows (keep top-level only).
                if (NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOT) != hWnd)
                    return true;

                if (!seen.Add(hWnd.ToInt64()))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0)
                    return true;
                // Don't list ourselves.
                if ((int)pid == selfPid)
                    return true;

                bool visible = NativeMethods.IsWindowVisible(hWnd);
                bool minimized = NativeMethods.IsIconic(hWnd);

                // Include visible and minimized windows. Skip only fully hidden
                // non-minimized HWNDs (most are system/helper windows).
                if (!visible && !minimized)
                    return true;

                int len = NativeMethods.GetWindowTextLengthW(hWnd);
                string title = "";
                if (len > 0)
                {
                    var sb = new StringBuilder(len + 1);
                    NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                    title = sb.ToString().Trim();
                }

                // Tool windows without a title are almost always UI chrome — skip those only.
                long exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                bool isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
                bool isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
                if (isToolWindow && !isAppWindow && string.IsNullOrEmpty(title))
                    return true;

                string procName = "";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch { }

                result.Add(new CapturableWindow
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessName = procName,
                    ProcessId = (int)pid,
                    IsMinimized = minimized
                });

                return true;
            }, IntPtr.Zero);

            // Stable order: title then process name.
            result.Sort((a, b) =>
            {
                int c = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }
    }
}
