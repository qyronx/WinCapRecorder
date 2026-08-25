using System;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace WinCapRecorder
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (s, ex) =>
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:O}] UI: {ex.Exception}\n\n"); } catch { }
                System.Windows.MessageBox.Show("예상치 못한 오류가 발생했습니다:\n" + ex.Exception.Message,
                    "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var exObj = ex.ExceptionObject as Exception;
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:O}] FATAL: {exObj}\n\n"); } catch { }
            };

            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now:O}] TASK: {ex.Exception}\n\n"); } catch { }
                ex.SetObserved();
            };
        }
    }
}
