using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OftScrubber.Preview
{
    /// <summary>
    /// A WinForms WebBrowser locked down for untrusted email HTML. The host answers the
    /// DISPID_AMBIENT_DLCONTROL ambient property, which MSHTML reads before loading a document:
    /// no scripts, Java, ActiveX, binary behaviours, meta refresh or frames, and offline mode so
    /// nothing is fetched from the network. Every navigation other than the blank page a
    /// document loads through is cancelled, so clicking a link in the preview goes nowhere.
    ///
    /// The WPF WebBrowser cannot supply ambient properties, which is why this is WinForms.
    /// </summary>
    public class SafeWebBrowser : WebBrowser
    {
        [Flags]
        private enum DownloadControl
        {
            Images = 0x10,
            NoScripts = 0x80,
            NoJava = 0x100,
            NoRunActiveXControls = 0x200,
            NoDownloadActiveXControls = 0x400,
            NoFrameDownload = 0x1000,
            NoBehaviors = 0x8000,
            NoFrames = 0x80000,
            ForceOffline = 0x10000000,
            NoClientPull = 0x20000000,
            Silent = 0x40000000,
        }

        private const DownloadControl Policy =
            DownloadControl.Images
            | DownloadControl.NoScripts
            | DownloadControl.NoJava
            | DownloadControl.NoRunActiveXControls
            | DownloadControl.NoDownloadActiveXControls
            | DownloadControl.NoFrameDownload
            | DownloadControl.NoBehaviors
            | DownloadControl.NoFrames
            | DownloadControl.ForceOffline
            | DownloadControl.NoClientPull
            | DownloadControl.Silent;

        private const int DispidAmbientDownloadControl = -5512;

        [ComImport]
        [Guid("B196B288-BAB4-101A-B69C-00AA00341D07")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IOleControl
        {
            [PreserveSig] int GetControlInfo(IntPtr controlInfo);
            [PreserveSig] int OnMnemonic(IntPtr message);
            [PreserveSig] int OnAmbientPropertyChange(int dispatchId);
            [PreserveSig] int FreezeEvents(int freeze);
        }

        public SafeWebBrowser()
        {
            ScriptErrorsSuppressed = true;
            AllowWebBrowserDrop = false;
            IsWebBrowserContextMenuEnabled = false;
            WebBrowserShortcutsEnabled = false;
            AllowNavigation = true; // Needed for DocumentText; Navigating below does the gating.
        }

        /// <summary>Shows HTML that has already been through PreviewHtml.Build.</summary>
        public void ShowHtml(string html)
        {
            // Ask the control to re-read the policy before each document, not only at creation.
            if (ActiveXInstance is IOleControl control) control.OnAmbientPropertyChange(DispidAmbientDownloadControl);
            DocumentText = html;
        }

        /// <summary>Raised when a link click was blocked, so the UI can explain why nothing happened.</summary>
        public event EventHandler? NavigationBlocked;

        [DllImport("ole32.dll")]
        private static extern int RevokeDragDrop(IntPtr window);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        /// <summary>
        /// MSHTML registers its own drop target even with AllowWebBrowserDrop off, and refuses
        /// files. Revoking it lets OLE pass the drag to the parent window, so a template dropped
        /// on the preview opens like one dropped anywhere else. Documents can re-register, so
        /// this runs after every load.
        /// </summary>
        private void ReleaseDropTargets()
        {
            if (!IsHandleCreated) return;
            EnumChildWindows(Handle, (window, parameter) => { RevokeDragDrop(window); return true; }, IntPtr.Zero);
        }

        protected override void OnDocumentCompleted(WebBrowserDocumentCompletedEventArgs e)
        {
            base.OnDocumentCompleted(e);
            ReleaseDropTargets();
        }

        protected override void OnNavigating(WebBrowserNavigatingEventArgs e)
        {
            // DocumentText loads by way of about:blank, and loads can overlap while the user
            // types, so a bare about:blank is always allowed. It can only show an empty page.
            if (e.Url != null && e.Url.ToString().Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                base.OnNavigating(e);
                return;
            }

            e.Cancel = true;
            NavigationBlocked?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnNewWindow(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            NavigationBlocked?.Invoke(this, EventArgs.Empty);
        }

        protected override WebBrowserSiteBase CreateWebBrowserSiteBase()
        {
            return new LockedDownSite(this);
        }

        /// <summary>
        /// MSHTML reads ambient properties through IDispatch on the client site. Exposing this
        /// dispatch interface as the site's default makes DISPID_AMBIENT_DLCONTROL answerable.
        /// </summary>
        [ComVisible(true)]
        [Guid("4B0B6E3E-7C9C-4C39-9E6B-1D2F6C1E5A01")]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface IAmbientDownloadControl
        {
            [DispId(-5512)]
            int DownloadControl { get; }
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        [ComDefaultInterface(typeof(IAmbientDownloadControl))]
        protected class LockedDownSite : WebBrowserSite, IAmbientDownloadControl, ICustomQueryInterface
        {
            private static readonly Guid DispatchInterface = new Guid("00020400-0000-0000-C000-000000000046");

            public LockedDownSite(WebBrowser host) : base(host)
            {
            }

            /// <summary>
            /// WinForms' site is not COM-visible, so a plain QueryInterface for IDispatch finds
            /// nothing. Answer it explicitly with the ambient-property interface.
            /// </summary>
            public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
            {
                ppv = IntPtr.Zero;
                if (iid != DispatchInterface) return CustomQueryInterfaceResult.NotHandled;

                ppv = Marshal.GetComInterfaceForObject(this, typeof(IAmbientDownloadControl), CustomQueryInterfaceMode.Ignore);
                return CustomQueryInterfaceResult.Handled;
            }

            public int DownloadControl => (int)Policy;
        }
    }
}
