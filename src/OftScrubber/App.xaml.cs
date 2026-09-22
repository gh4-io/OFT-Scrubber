using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace OftScrubber
{
    public partial class App : Application
    {
        /// <summary>Files passed on the command line, e.g. when .oft files are dropped on the exe.</summary>
        public static string[] StartupFiles { get; private set; } = new string[0];

        protected override void OnStartup(StartupEventArgs e)
        {
            StartupFiles = e.Args ?? new string[0];

            DispatcherUnhandledException += OnUnhandledException;

            base.OnStartup(e);
        }

        /// <summary>
        /// A crash dialog beats a silent disappearance: this app is launched by double-click, so
        /// there is no console to catch the stack trace.
        /// </summary>
        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var message = new StringBuilder();
            message.AppendLine("Something went wrong and the action was stopped.");
            message.AppendLine();
            message.AppendLine(e.Exception.GetType().Name + ": " + e.Exception.Message);

            if (e.Exception.StackTrace != null)
            {
                message.AppendLine();
                message.AppendLine(e.Exception.StackTrace);
            }

            MessageBox.Show(message.ToString(), "OFT Scrubber", MessageBoxButton.OK, MessageBoxImage.Warning);

            e.Handled = true;
        }
    }
}
