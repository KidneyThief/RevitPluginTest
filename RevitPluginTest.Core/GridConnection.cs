using Autodesk.Revit.DB;

namespace RevitPluginTest.Core
{
    public enum GridConnectionDirection
    {
        Up,
        Down,
        Left,
        Right
    }

    public sealed class GridConnection
    {
        public XYZ _connectLocation { get; }
        public XYZ _neighborLocation { get; }
        public GridConnectionDirection _connectionDirection { get; }

        public GridConnection(XYZ connectLocation, XYZ neighborLocation, GridConnectionDirection connectionDirection)
        {
            _connectLocation = connectLocation;
            _neighborLocation = neighborLocation;
            _connectionDirection = connectionDirection;
        }
    }
}
