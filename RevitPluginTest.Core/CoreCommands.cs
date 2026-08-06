using Autodesk.Revit.UI;
using RevitPluginTest;

namespace RevitPluginTest.Core
{
    // Mirrors HostCommands, but for logic that's worth being able to edit
    // and reload without restarting Revit.
    public static class CoreCommands
    {
        [Schedulable("HelloWorld")]
        public static bool HelloWorld()
        {
            TaskDialog.Show("My Plugin", "Hello World! This is my first Revit plugin. *RELOADED again*");
            return true;
        }
    }
}
