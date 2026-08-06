using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitPluginTest;

namespace RevitPluginTest.Core
{
    public static class GraphTestFunctions
    {
        // Core-owned instance, referenced only by Core's own code (this class)
        // - safe across reload for the same reason DebugOverlay._window is:
        // when the ALC unloads, this field and everything it points to are
        // collected together. Its contents don't survive a reload though,
        // same as DebugDraw's primitives - ReloadCore() tears down the whole
        // assembly this lives in.
        private static readonly ConduitGraphTest _graph = new();

        [Schedulable("DrawGraph")]
        public static bool DrawGraph()
        {
            _graph.DrawGraph();
            return true;
        }

        // Called by CoreLifecycle.Initialize() before Unload() - not
        // individually schedulable, since Initialize() is the one reload-time
        // entry point Host knows about. Not strictly required for pure
        // in-memory data (the ALC unload would collect it regardless), but
        // makes the cleanup visible and gives future graph state - anything
        // that does need explicit teardown - a hook to plug into.
        public static void ResetGraph()
        {
            _graph.ClearGraph();
        }

        // First parameter is UIApplication, so Scheduler.Invoke supplies it
        // automatically - type SelectAllSimilar() in the console or click
        // "Select All", no need to pass one yourself. Takes the type of
        // whatever's currently selected (the first element, if several are
        // selected) and replaces the selection with every instance of that
        // same type in the project.
        [Schedulable("SelectAllSimilar")]
        public static bool SelectAllSimilar(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;

            if (uidoc == null)
            {
                Logger.Log("SelectAllSimilar: no active document.");
                return false;
            }

            var selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
            {
                Logger.Log("SelectAllSimilar: nothing selected.");
                return false;
            }

            var document = uidoc.Document;
            var reference = document.GetElement(selectedIds.First());
            var typeId = reference?.GetTypeId();

            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                Logger.Log("SelectAllSimilar: selected element has no type.");
                return false;
            }

            var matches = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .Where(e => e.GetTypeId() == typeId)
                .Select(e => e.Id)
                .ToList();

            uidoc.Selection.SetElementIds(matches);
            Logger.Log($"SelectAllSimilar: selected {matches.Count} instance(s).");

            return true;
        }
    }
}
