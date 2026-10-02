using Autodesk.Revit.DB;

namespace RevitPluginTest.Core
{
    // Compass directions - N/S/E/W are the only ones TryConnect currently
    // produces (cells only connect along shared axis-aligned edges), but the
    // diagonals are declared now for connect directions still to come.
    public enum GridConnectionDirection
    {
        // First (so it's the enum's default value, 0) - means "no direction
        // yet", used by a search's start node, which arrived from nowhere
        // and so has nothing to double back on and no turn penalty.
        None,
        N,
        S,
        E,
        W,
        NE,
        SE,
        SW,
        NW
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
