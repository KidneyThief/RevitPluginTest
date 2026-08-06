using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitPluginTest
{
    [Transaction(TransactionMode.Manual)]
    public class ReloadCoreCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            HostCommands.ReloadCore();

            TaskDialog.Show("Reload Core", "Core assembly reloaded.");

            return Result.Succeeded;
        }
    }
}
