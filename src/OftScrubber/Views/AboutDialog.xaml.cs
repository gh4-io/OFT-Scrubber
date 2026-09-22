using System;
using System.Reflection;
using System.Windows;

namespace OftScrubber.Views
{
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();

            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "Unknown";

            VersionText.Text = "Version " + version;

            RuntimeText.Text = ".NET Framework " + Environment.Version
                + ", which ships with Windows. Nothing else is installed.";
        }
    }
}
