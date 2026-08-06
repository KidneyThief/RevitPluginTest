using RevitPluginTest;

namespace RevitPluginTest.Core
{
    // Declares Core's desired panel widgets - called automatically by the
    // host on every load/reload (RevitPluginTestApplication.LoadCore), so the
    // panel's dynamic section tracks whatever Core currently defines without
    // ever needing a Revit restart.
    public static class PanelWidgets
    {
        [Schedulable("RegisterPanelWidgets", Quiet = true)]
        public static bool RegisterPanelWidgets()
        {
            DynamicPanel.AddButton("Graph Tests", "Select Similar", "SelectAllSimilar");
            return true;
        }
    }
}
