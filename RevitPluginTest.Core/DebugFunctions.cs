using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitPluginTest;
using Color = System.Windows.Media.Color;

namespace RevitPluginTest.Core
{
    public static class DebugFunctions
    {
        [Schedulable("DrawOrigin")]
        public static bool DrawOrigin()
        {
            DebugDraw.Circle(null, XYZ.Zero, 2.0);
            DebugDraw.Text(null, XYZ.Zero, "Origin");
            return true;
        }

        // Thin bool-returning wrappers over DebugDraw's int-returning (element
        // id) API, so these are usable through Scheduler.Invoke/Schedule like
        // any other command - e.g. typed into the console as
        // DrawCircle(XYZ(0, 5, 10), 2.5, Color.Red) or
        // DrawCircle(XYZ(0, 5, 10), 2.5, Color(255, 128, 0)).
        [Schedulable("DrawCircle")]
        public static bool DrawCircle(XYZ center, double radius, Color? color = null, double thickness = 2, double duration = -1)
        {
            DebugDraw.Circle(null, center, radius, color, thickness, duration);
            return true;
        }

        [Schedulable("DrawLine")]
        public static bool DrawLine(XYZ start, XYZ end, Color? color = null, double thickness = 2, double duration = -1)
        {
            DebugDraw.Line(null, start, end, color, thickness, duration);
            return true;
        }

        [Schedulable("DrawText")]
        public static bool DrawText(XYZ position, string text, Color? color = null, double thickness = 12, double duration = -1)
        {
            DebugDraw.Text(null, position, text, color, thickness, duration);
            return true;
        }

        [Schedulable("DrawArrow")]
        public static bool DrawArrow(XYZ start, XYZ end, Color? color = null, double thickness = 2, double duration = -1)
        {
            DebugDraw.Arrow(null, start, end, color, thickness, duration);
            return true;
        }

        // First parameter is UIApplication, so Scheduler.Invoke supplies it
        // automatically - type DrawSelected() or DrawSelected(Color.Red) in
        // the console, no need to pass one yourself.
        [Schedulable("DrawSelected")]
        public static bool DrawSelected(UIApplication uiApp, Color? color = null, double thickness = 2, double duration = -1)
        {
            var uidoc = uiApp.ActiveUIDocument;

            if (uidoc == null)
            {
                Logger.Log("DrawSelected: no active document.");
                return false;
            }

            var selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
            {
                Logger.Log("DrawSelected: nothing selected.");
                return false;
            }

            foreach (var id in selectedIds)
            {
                var bbox = uidoc.Document.GetElement(id)?.get_BoundingBox(null);

                if (bbox == null)
                {
                    continue;
                }

                var center = (bbox.Min + bbox.Max).Multiply(0.5);
                var radius = bbox.Min.DistanceTo(bbox.Max) / 2;

                DebugDraw.Circle(null, center, radius, color, thickness, duration);
            }

            return true;
        }
    }
}
