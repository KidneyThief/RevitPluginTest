using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Point = System.Windows.Point;

namespace RevitPluginTest.Core
{
    // Projects a model-space point to screen pixel coordinates for a given
    // view. Only orthographic (parallel) projection is supported - Revit's
    // public API doesn't expose the perspective/FOV data needed to project
    // points correctly for a perspective 3D view.
    public static class ViewProjector
    {
        public static bool IsSupported(View view)
        {
            return !(view is View3D view3D && view3D.IsPerspective);
        }

        public static bool TryProject(View view, UIView uiView, XYZ point, out Point screenPoint)
        {
            screenPoint = default;

            if (!IsSupported(view))
            {
                return false;
            }

            var corners = uiView.GetZoomCorners();
            var bottomLeft = corners[0];
            var topRight = corners[1];

            var right = view.RightDirection;
            var up = view.UpDirection;

            var minU = bottomLeft.DotProduct(right);
            var maxU = topRight.DotProduct(right);
            var minV = bottomLeft.DotProduct(up);
            var maxV = topRight.DotProduct(up);

            var pu = point.DotProduct(right);
            var pv = point.DotProduct(up);

            var rect = uiView.GetWindowRectangle();

            var fx = (pu - minU) / (maxU - minU);
            var fy = (pv - minV) / (maxV - minV);

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            var x = rect.Left + fx * width;
            var y = rect.Top + (1 - fy) * height; // screen Y grows downward

            screenPoint = new Point(x, y);
            return true;
        }
    }
}
