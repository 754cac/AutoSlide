using System;
using System.Threading.Tasks;
using Office = Microsoft.Office.Core;

namespace PowerPointSharing
{
    public partial class ThisAddIn
    {
        private readonly string _backendBaseUrl = "http://127.0.0.1:5000/";

        private PresentationSharingRibbon? _ribbon;

        internal SessionManager       Session     { get; private set; }
        internal SignalRService        SignalR     { get; private set; }
        internal InkOverlayService     InkOverlay  { get; private set; }
        internal SpeechService         Speech      { get; private set; }
        internal SlideShowEventHandler SlideEvents { get; private set; }

        public bool IsSharing => Session?.IsSharing ?? false;

        public async Task StartPresentationSession(string courseId, string authToken)
        {
            await Session.StartSession(courseId, authToken);
        }

        public void StopSharing()
        {
            Session?.StopSharing();
        }

        public void CreateBlankSolutionPage()
        {
            Session?.CreateBlankSolutionPage();
        }

        public void CreateCurrentSlideSolutionPage()
        {
            Session?.CreateCurrentSlideSolutionPage();
        }

        protected override Office.IRibbonExtensibility CreateRibbonExtensibilityObject()
        {
            _ribbon = new PresentationSharingRibbon();
            return _ribbon;
        }

        private void ThisAddIn_Startup(object sender, EventArgs e)
        {
            try
            {
                SignalR    = new SignalRService(_backendBaseUrl);
                InkOverlay = new InkOverlayService();
                Speech     = new SpeechService();
                Session    = new SessionManager(Application, SignalR, InkOverlay, Speech, _backendBaseUrl);
                Session.SharingStateChanged += OnSharingStateChanged;
                SlideEvents = new SlideShowEventHandler(Application, Session, InkOverlay);

                Session.RegisterEventHandler(SlideEvents);

                Application.SlideShowBegin     += SlideEvents.OnSlideShowBegin;
                Application.SlideShowNextSlide += SlideEvents.OnSlideShowNextSlide;
                Application.SlideShowEnd       += SlideEvents.OnSlideShowEnd;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PowerPointSharing] Failed to initialize: {ex}");
            }
        }

        private void ThisAddIn_Shutdown(object sender, EventArgs e)
        {
            try
            {
                try { StopSharing(); } catch { }

                if (Session != null)
                {
                    Session.SharingStateChanged -= OnSharingStateChanged;
                }

                Application.SlideShowBegin     -= SlideEvents.OnSlideShowBegin;
                Application.SlideShowNextSlide -= SlideEvents.OnSlideShowNextSlide;
                Application.SlideShowEnd       -= SlideEvents.OnSlideShowEnd;
            }
            catch { }
        }

        private void OnSharingStateChanged(object? sender, bool isSharing)
        {
            _ribbon?.InvalidateSolutionControls();

            if (!isSharing)
            {
                _ribbon?.InvalidateShareToggle();
            }
        }

        private void OnSharingStateChanged(object? sender, bool? isSharing)
        {
            OnSharingStateChanged(sender, isSharing ?? false);
        }

        private void InternalStartup()
        {
            this.Startup += new System.EventHandler(ThisAddIn_Startup);
            this.Shutdown += new System.EventHandler(ThisAddIn_Shutdown);
        }
    }
}