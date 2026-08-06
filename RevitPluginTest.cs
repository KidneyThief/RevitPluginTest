using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitPluginTest
{
    // Define the transaction behavior (required)
    [Transaction(TransactionMode.Manual)]
    public class HelloWorldCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            RevitPluginTestApplication.Current?.InvokeCommand("HelloWorld");

            return Result.Succeeded;
        }
    }
}
