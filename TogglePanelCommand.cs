using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitPluginTest
{
    [Transaction(TransactionMode.Manual)]
    public class TogglePanelCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            var pane = commandData.Application.GetDockablePane(RevitPluginTestApplication.PanelId);

            if (pane.IsShown())
            {
                pane.Hide();
            }
            else
            {
                pane.Show();
            }

            return Result.Succeeded;
        }
    }
}
