using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Office = Microsoft.Office.Core;

namespace PowerPointSharing
{
    [ComVisible(true)]
    public class PresentationSharingRibbon : Office.IRibbonExtensibility
    {
        private Office.IRibbonUI? _ribbonUI;

        public PresentationSharingRibbon()
        {
        }

        public string GetCustomUI(string ribbonID)
        {
            return @"<customUI xmlns=""http://schemas.microsoft.com/office/2009/07/customui"" onLoad=""Ribbon_Load""> 
  <ribbon>
    <tabs>
      <tab id=""tabPowerPointSharing"" label=""Sharing"">
        <group id=""grpSession"" label=""Live Session"">
          <toggleButton id=""tglShare""
                        label=""Start Presentation""
                        size=""large""
                        imageMso=""BroadcastSlideShow""
                        onAction=""OnShareToggle""
                        getPressed=""GetSharePressed"" />
        </group>
      </tab>
    </tabs>
  </ribbon>
</customUI>";
        }

        public void Ribbon_Load(Office.IRibbonUI ribbonUI)
        {
            _ribbonUI = ribbonUI;
        }

        public async void OnShareToggle(Office.IRibbonControl control, bool isStartingSession)
        {
            var addIn = Globals.ThisAddIn;

            if (isStartingSession)
            {
                var authenticationDialog = new AuthenticationDialog("http://127.0.0.1:5000");
                var dialogResult = authenticationDialog.ShowDialog();

                if (dialogResult == DialogResult.OK)
                {
                    await addIn.StartPresentationSession(authenticationDialog.SelectedCourseId, authenticationDialog.AuthToken);
                }
                else
                {
                    addIn.StopSharing();
                    _ribbonUI?.InvalidateControl("tglShare");
                }
            }
            else
            {
                addIn.StopSharing();
            }
        }

        public bool GetSharePressed(Office.IRibbonControl control)
        {
            return Globals.ThisAddIn.IsSharing;
        }

        public bool GetSolutionCommandsEnabled(Office.IRibbonControl control)
        {
            return Globals.ThisAddIn.IsSharing;
        }

        public void OnCreateBlankSolutionPage(Office.IRibbonControl control)
        {
            try
            {
                Globals.ThisAddIn.CreateBlankSolutionPage();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to create blank solution page: " + ex.Message, "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void OnCreateCurrentSlideSolutionPage(Office.IRibbonControl control)
        {
            try
            {
                Globals.ThisAddIn.CreateCurrentSlideSolutionPage();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to create current-slide solution page: " + ex.Message, "Solution Page", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void InvalidateShareToggle()
        {
            _ribbonUI?.InvalidateControl("tglShare");
        }

        public void InvalidateSolutionControls()
        {
            _ribbonUI?.InvalidateControl("btnBlankSolution");
            _ribbonUI?.InvalidateControl("btnCurrentSlideSolution");
        }
    }
}
