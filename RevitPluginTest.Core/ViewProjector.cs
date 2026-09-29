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

        // Inverse of TryProject, restricted to plan-like views (view.Right
        // and view.Up both horizontal). In that case right/up form an
        // orthonormal basis for the world XY plane by themselves - a
        // point's dot products with them (pu, pv) depend only on its X/Y,
        // never its Z - so reconstructing world = right*pu + up*pv always
        // lands on Z=0 regardless of the view's actual elevation. For any
        // other view orientation, the screen position alone can't
        // determine where along the view's depth axis a Z=0 point would
        // be, so this returns false rather than guessing.
        public static bool TryUnprojectPlanView(View view, UIView uiView, Point screenPoint, out XYZ worldPoint)
        {
            worldPoint = XYZ.Zero;

            if (!IsSupported(view))
            {
                return false;
            }

            var right = view.RightDirection;
            var up = view.UpDirection;

            const double epsilon = 1e-6;

            if (Math.Abs(right.Z) > epsilon || Math.Abs(up.Z) > epsilon)
            {
                return false;
            }

            var corners = uiView.GetZoomCorners();
            var bottomLeft = corners[0];
            var topRight = corners[1];

            var minU = bottomLeft.DotProduct(right);
            var maxU = topRight.DotProduct(right);
            var minV = bottomLeft.DotProduct(up);
            var maxV = topRight.DotProduct(up);

            var rect = uiView.GetWindowRectangle();
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var fx = (screenPoint.X - rect.Left) / width;
            var fy = 1 - (screenPoint.Y - rect.Top) / height; // screen Y grows downward

            var pu = minU + fx * (maxU - minU);
            var pv = minV + fy * (maxV - minV);

            worldPoint = right.Multiply(pu) + up.Multiply(pv);
            return true;
        }
    }
}
