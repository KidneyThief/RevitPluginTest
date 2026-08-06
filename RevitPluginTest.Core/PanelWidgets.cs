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
            // Headerless row - sits directly below the static Debug Overlay
            // row rather than under its own section label.
            DynamicPanel.AddButton(null, "Select Similar", "SelectAllSimilar");
            DynamicPanel.AddButton(null, "Draw Selected", "DrawSelected");

            DynamicPanel.AddButton("Graph Tests", "Add Selected", "AddSelectedToGraph");
            DynamicPanel.AddButton("Graph Tests", "Build Graph", "BuildGraph");
            DynamicPanel.AddButton("Graph Tests", "Draw Graph", "DrawGraph");
            return true;
        }
    }
}
