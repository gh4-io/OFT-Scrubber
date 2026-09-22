using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualBasic.ApplicationServices;

namespace OftScrubber
{
    // Use the .NET Framework application model for arbitration, startup races,
    // and argument forwarding.
    internal sealed class SingleInstanceApplication : WindowsFormsApplicationBase
    {
        private App? _application;

        private SingleInstanceApplication()
        {
            IsSingleInstance = true;
            EnableVisualStyles = false;
        }

        [STAThread]
        public static void Main(string[] args)
        {
            // Resolve paths in the launching process, before forwarding to an
            // existing instance that may have a different working directory.
            for (var i = 0; i < args.Length; i++)
            {
                try { args[i] = Path.GetFullPath(args[i]); }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            // Pass the launcher's foreground permission to the receiving process.
            // Windows revokes this permission on the next unrelated user input.
            AllowSetForegroundWindow(-1); // ASFW_ANY
            new SingleInstanceApplication().Run(args);
        }

        protected override void OnRun()
        {
            _application = new App();
            _application.InitializeComponent();
            _application.Run();
        }

        protected override void OnStartupNextInstance(StartupNextInstanceEventArgs eventArgs)
        {
            // There is no WinForms main form. WPF owns the window and message loop.
            eventArgs.BringToForeground = false;
            _application?.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!(_application.MainWindow is MainWindow window)) return;
                var handle = new WindowInteropHelper(window).Handle;
                if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE preserves restore-to-maximized.

                // An owned modal dialog must retain focus while its owner is disabled.
                var target = GetLastActivePopup(handle);
                SetForegroundWindow(target);
                if (target == handle) window.Activate();

                if (eventArgs.CommandLine.Count > 0)
                    window.AddFiles(eventArgs.CommandLine);
            }));
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);
    }
}
