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

            DynamicPanel.AddButton("Graph Tests", "Initialize", "Initialize");
            DynamicPanel.AddButton("Graph Tests", "Add Sources", "AddSourcesToGraph");
            DynamicPanel.AddButton("Graph Tests", "Add Receptacles", "AddReceptaclesToGraph");

            DynamicPanel.NewLine("Graph Tests");
            DynamicPanel.AddButton("Graph Tests", "Find Nearest Source", "FindNearestSource");
            DynamicPanel.AddButton("Graph Tests", "Draw Nodes", "DrawNodes");

            DynamicPanel.NewLine("Graph Tests");
            DynamicPanel.AddButton("Graph Tests", "Build Graph", "BuildGraph");
            DynamicPanel.AddButton("Graph Tests", "Draw Graph", "DrawGraph");

            DynamicPanel.NewLine("Graph Tests");
            DynamicPanel.AddCheckbox("Graph Tests", "Allow Intersections", GraphTestFunctions.AllowIntersections, "SetAllowIntersections");

            DynamicPanel.NewLine("Graph Tests");
            DynamicPanel.AddSlider("Graph Tests", "Run Tolerance:", 0, 10, GraphTestFunctions.RunTolerance, "SetRunTolerance");

            DynamicPanel.AddSlider("Grid Tests", "Grid Resolution:", 4, 24, GridTest.GridResolution, "SetGridResolution", step: 4);

            DynamicPanel.NewLine("Grid Tests");
            DynamicPanel.AddButton("Grid Tests", "Create Grid", "CreateGrid");
            DynamicPanel.AddButton("Grid Tests", "Draw Grid", "DrawGrid");
            DynamicPanel.AddCheckbox("Grid Tests", "Connections", GridTest.ShowConnections, "SetShowConnections");

            DynamicPanel.NewLine("Grid Tests");
            DynamicPanel.AddSlider("Grid Tests", "Obstruction Size:", 5, 24, GridTest.ObstructionSize, "SetObstructionSize", step: 1);
            DynamicPanel.NewLine("Grid Tests");
            DynamicPanel.AddObstructionButton("Grid Tests", "Add Obstruction", "Adding...", "AddObstruction");
            return true;
        }
    }
}
