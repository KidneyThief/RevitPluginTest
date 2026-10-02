namespace RevitPluginTest.Core
{
    // A search state, not just a cell - the same cell reached via different
    // connect directions, or with a different _distanceFromLastTurn, is a
    // different node. Which way we arrived constrains which way we're
    // allowed to leave (no doubling back), and _distanceFromLastTurn gates
    // whether another turn is even permitted yet (see TurnMinDistance).
    // _parent chains back to the start node (whose _parent is null) so a
    // found path can be reconstructed by walking backward once the
    // destination is reached.
    public sealed class GridSearchNode
    {
        public GridCell _cell { get; }
        public GridSearchNode? _parent { get; }
        public GridConnectionDirection _connectDirection { get; }
        public double _costFromStart { get; }
        public double _estimateToDestination { get; }
        public int _turnCount { get; }

        // Linear distance traveled since the most recent turn (or since the
        // start, if there hasn't been one yet). Another turn isn't allowed
        // until this reaches GridTest's TurnMinDistance.
        public double _distanceFromLastTurn { get; }

        // The specific connection (from _parent._cell) that was followed to
        // reach this node - null for the start node, which didn't arrive via
        // any connection. Lets path drawing find each hop's exact edge point
        // (_connectLocation) without re-searching _parent._cell.Connections.
        public GridConnection? _connection { get; }

        // f = g + h - what the search heap orders by.
        public double TotalCost => _costFromStart + _estimateToDestination;

        public GridSearchNode(GridCell cell, GridSearchNode? parent, GridConnection? connection, GridConnectionDirection connectDirection, double costFromStart, double estimateToDestination, int turnCount, double distanceFromLastTurn)
        {
            _cell = cell;
            _parent = parent;
            _connection = connection;
            _connectDirection = connectDirection;
            _costFromStart = costFromStart;
            _estimateToDestination = estimateToDestination;
            _turnCount = turnCount;
            _distanceFromLastTurn = distanceFromLastTurn;
        }
    }
}
