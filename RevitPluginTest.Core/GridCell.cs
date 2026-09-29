using Autodesk.Revit.DB;

namespace RevitPluginTest.Core
{
    public sealed class GridCell
    {
        public XYZ MinXY { get; }
        public XYZ MaxXY { get; }

        public XYZ Location => (MinXY + MaxXY).Multiply(0.5);

        // Populated after construction, once every cell in the grid exists -
        // adjacency can't be known at the moment a single cell is created.
        public List<GridConnection> Connections { get; } = new();

        public GridCell(XYZ minXY, XYZ maxXY)
        {
            MinXY = minXY;
            MaxXY = maxXY;
        }
    }
}
