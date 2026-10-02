using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace RevitPluginTest.Core
{
    public static class GridTest
    {
        private const double GridWidth = 300.0;
        private const double GridHeight = 100.0;

        private static int _gridResolution = 4;
        private static int _obstructionSize = 5;

        // Minimum straight-line distance required after a turn before
        // another turn is permitted (see TryCreateSuccessor).
        private static int _turnMinDistance = 1;

        // Core-owned, same reload-safety reasoning as GraphTestFunctions'
        // _graph field - doesn't survive a reload, but doesn't need to.
        //
        // Cell locations bucketed by a spatial hash instead of one flat
        // list - the bucket for a location is (floor(x / BucketSize),
        // floor(y / BucketSize)), with BucketSize = GridResolution (see
        // BucketKey/BucketSize). For any location, the 3x3 block of buckets
        // centered on its own bucket (NearbyGridPoints) is then guaranteed
        // to contain every cell that could possibly be edge-adjacent to it -
        // including two coarse cells, whose centers can be as far apart as
        // GridResolution itself.
        private static readonly Dictionary<(int X, int Y), List<XYZ>> _gridPointMap = new();
        private static int _gridPointCount;

        // Keyed by a rounded coordinate tuple rather than XYZ itself - same
        // reasoning as GraphTestFunctions.RoundKey: XYZ doesn't give a
        // reliable value-based Equals/GetHashCode to key a dictionary with.
        private static readonly Dictionary<(long X, long Y, long Z), GridCell> _cells = new();

        // Cells that block routing - same GridCell type and key convention
        // as _cells, but a separate map since a location can be a grid cell
        // without being an obstruction (and vice versa, e.g. an obstruction
        // that doesn't align with the grid).
        private static readonly Dictionary<(long X, long Y, long Z), GridCell> _obstructions = new();

        private static (long X, long Y, long Z)? _highlightedKey;
        private static Color? _highlightedColor;

        // Armed by the "Add Obstruction"/"PathStart"/"PathEnd" buttons;
        // consumed by the next fresh left-click detected in
        // UpdateClickPlacement. _wasLeftButtonDown tracks the button's state
        // from the previous tick so a held-down click (e.g. the one that
        // pressed the arming button itself) doesn't re-trigger - only a
        // 0->1 transition counts.
        private static bool _waitingForObstructionClick;
        private static bool _waitingForPathStartClick;
        private static bool _waitingForPathEndClick;
        private static bool _wasLeftButtonDown;

        // Cached grid-cell locations for the pathfinding pass across the
        // connection graph. Cleared whenever the grid regenerates (see
        // CreateGrid) - a location from before a regenerate might no longer
        // correspond to a valid cell.
        private static XYZ? _pathStart;
        private static XYZ? _pathEnd;

        // The most recently found path, as an ordered list of connections
        // from the start cell to the goal cell - empty if none has been
        // found yet, or if the grid regenerated since (see CreateGrid).
        // DrawPath derives every waypoint from this rather than storing them
        // separately - each connection's own _connectLocation and
        // _neighborLocation are exactly the mid-route and arrival points for
        // that hop.
        private static readonly List<GridConnection> _path = new();

        // Set when the most recent FindPath genuinely exhausted the search
        // without reaching the goal (not when it never ran at all, e.g.
        // PathStart/PathEnd unset) - DrawPath uses this to show a red line
        // straight from _pathStart to _pathEnd as a "no route found"
        // indicator instead of the normal green route.
        private static bool _pathSearchFailed;

        // Toggled by the "Connections" checkbox - swaps DrawGrid between its
        // normal cell rectangles and a view of just the connection graph.
        // Obstructions are drawn either way (see DrawGrid).
        private static bool _showConnections;

        public static int GridResolution => _gridResolution;

        // No longer independently settable - always the smallest whole
        // divisor of GridResolution that's still >= 4, i.e. the finest
        // subdivision possible without dropping under FineResolution's own
        // minimum. E.g. GridResolution 15 -> 5 (not 15 - 3 divides 15 and
        // gives exactly 5, finer than the no-op of not subdividing at all,
        // and no smaller divisor of 15 reaches 4). GridResolution 20 -> 4
        // (4 itself divides 20 evenly, so there's no need to settle for the
        // coarser 5 that GridResolution / 4 alone would suggest).
        // GridResolution below 8 has no divisor besides itself that's >= 4
        // (half of anything under 8 is under 4), so FineResolution just
        // equals GridResolution there - subdivision near an obstruction
        // becomes a no-op in that case, since a "fine" cell the same size as
        // the coarse cell it replaces still overlaps whatever the coarse
        // cell did.
        public static int FineResolution
        {
            get
            {
                for (var candidate = 4; candidate < _gridResolution; candidate++)
                {
                    if (_gridResolution % candidate == 0)
                    {
                        return candidate;
                    }
                }

                return _gridResolution;
            }
        }

        public static int ObstructionSize => _obstructionSize;
        public static bool ShowConnections => _showConnections;
        public static int TurnMinDistance => _turnMinDistance;

        // No longer constrained to multiples of 4 - the panel slider reports
        // a raw double, rounded to the nearest whole number here rather than
        // relying on the slider to snap. FineResolution no longer needs
        // GridResolution to be a multiple of anything in particular; it
        // finds its own valid divisor (see its getter).
        [Schedulable("SetGridResolution", Quiet = true)]
        public static bool SetGridResolution(double resolution)
        {
            _gridResolution = Math.Max((int)Math.Round(resolution), 4);
            return true;
        }

        [Schedulable("SetObstructionSize", Quiet = true)]
        public static bool SetObstructionSize(double size)
        {
            _obstructionSize = (int)Math.Round(size);
            return true;
        }

        [Schedulable("SetTurnMinDistance", Quiet = true)]
        public static bool SetTurnMinDistance(double value)
        {
            _turnMinDistance = (int)Math.Round(value);
            return true;
        }

        // Redraws immediately on toggle, rather than waiting for the next
        // "Draw Grid" click, so the checkbox reads as a live view switch.
        [Schedulable("SetShowConnections", Quiet = true)]
        public static bool SetShowConnections(bool show)
        {
            _showConnections = show;
            DrawGrid();
            return true;
        }

        // Arms obstruction placement - the next fresh left-click detected in
        // UpdateClickPlacement (every Idling tick) places the obstruction
        // and disarms.
        [Schedulable("AddObstruction")]
        public static bool AddObstruction()
        {
            _waitingForObstructionClick = true;
            OverlayState.IsAddingObstruction = true;
            Logger.Log("AddObstruction: click a location in the view to place an obstruction.");
            return true;
        }

        // Arms path-start placement - the next fresh left-click detected in
        // UpdateClickPlacement caches the clicked grid cell's location as
        // _pathStart (only if the click actually landed inside a cell) and
        // disarms.
        [Schedulable("PathStart")]
        public static bool PathStart()
        {
            _waitingForPathStartClick = true;
            OverlayState.IsSettingPathStart = true;
            Logger.Log("PathStart: click a grid cell to set the path start.");
            return true;
        }

        [Schedulable("PathEnd")]
        public static bool PathEnd()
        {
            _waitingForPathEndClick = true;
            OverlayState.IsSettingPathEnd = true;
            Logger.Log("PathEnd: click a grid cell to set the path end.");
            return true;
        }

        // Squares of size GridResolution tile the 300"x100" area centered on
        // (0, 0). The half-extents (150, 50) are already whole numbers, so
        // stepping from -halfWidth/-halfHeight by GridResolution (itself
        // always a whole number) keeps every center on a whole-number
        // coordinate. Obstructions are NOT cleared here - they persist
        // across a regenerate, and every cell (coarse or fine) is checked
        // against them as it's created, so a recreated grid still respects
        // whatever's already been placed.
        //
        // A coarse cell is only used where nothing is within GridResolution
        // of any obstruction (IsNearAnyObstruction, buffered) - anywhere
        // closer falls back to AddFineCells. AddFineCells anchors its
        // sub-grid to the coarse cell's own edges, which are shared exactly
        // with every neighboring coarse cell, and FineResolution is always a
        // whole divisor of GridResolution (see the FineResolution property) -
        // so it divides evenly by construction and fine cells tile
        // seamlessly against both fine and coarse neighbors with no seam
        // gaps. The only gaps left are where a cell would actually overlap
        // the obstruction.
        [Schedulable("CreateGrid")]
        public static bool CreateGrid()
        {
            if (_gridResolution <= 0)
            {
                Logger.Log("CreateGrid: grid resolution must be greater than zero.");
                return false;
            }

            _gridPointMap.Clear();
            _gridPointCount = 0;
            _cells.Clear();
            _highlightedKey = null;
            _highlightedColor = null;
            _pathStart = null;
            _pathEnd = null;
            _path.Clear();
            _pathSearchFailed = false;

            var halfWidth = GridWidth / 2.0;
            var halfHeight = GridHeight / 2.0;
            var halfCell = _gridResolution / 2.0;
            var coarseCount = 0;
            var fineCount = 0;

            for (var x = -halfWidth; x <= halfWidth; x += _gridResolution)
            {
                for (var y = -halfHeight; y <= halfHeight; y += _gridResolution)
                {
                    var min = new XYZ(x - halfCell, y - halfCell, 0);
                    var max = new XYZ(x + halfCell, y + halfCell, 0);
                    var coarseCell = new GridCell(min, max);

                    if (!IsNearAnyObstruction(coarseCell, _gridResolution))
                    {
                        var point = new XYZ(x, y, 0);
                        AddGridPoint(point);
                        _cells[CellKey(point)] = coarseCell;
                        coarseCount++;
                    }
                    else
                    {
                        fineCount += AddFineCells(min, max);
                    }
                }
            }

            BuildConnections();

            Logger.Log($"CreateGrid: created {_gridPointCount} cell(s) ({coarseCount} at {_gridResolution}\" resolution, {fineCount} at {FineResolution}\" resolution near obstructions) over a {GridWidth}\"x{GridHeight}\" area centered at (0, 0).");

            DrawGrid();
            return true;
        }

        // Two coarse cells can be as far apart as GridResolution (their
        // half-widths sum to exactly that) - the maximum possible separation
        // between any pair of edge-adjacent cells in the grid. A 3x3 window
        // of buckets sized GridResolution/2 falls exactly one bucket short
        // of reaching that (two coarse cells GridResolution apart land 2
        // buckets apart, not 1), which is why connections were only ever
        // forming when a smaller fine cell was involved. Sizing buckets at
        // the full GridResolution instead guarantees the +/- BucketSize
        // search in NearbyGridPoints always reaches a same-size neighbor,
        // while still comfortably covering every smaller separation too.
        private static double BucketSize => _gridResolution;

        private static (int X, int Y) BucketKey(XYZ location)
        {
            var bucketSize = BucketSize;
            return ((int)Math.Floor(location.X / bucketSize), (int)Math.Floor(location.Y / bucketSize));
        }

        private static void AddGridPoint(XYZ point)
        {
            var key = BucketKey(point);

            if (!_gridPointMap.TryGetValue(key, out var bucket))
            {
                bucket = new List<XYZ>();
                _gridPointMap[key] = bucket;
            }

            bucket.Add(point);
            _gridPointCount++;
        }

        private static IEnumerable<XYZ> AllGridPoints() => _gridPointMap.Values.SelectMany(bucket => bucket);

        // Every grid point in the 9 buckets (3x3) around `location` - its
        // own bucket, plus the ones reached by stepping +/- BucketSize along
        // each axis. `location` doesn't need to be an existing grid point
        // itself - an arbitrary query point (e.g. the mouse) works the same
        // way, since the largest cell is exactly BucketSize wide, so
        // anything that could be edge-adjacent to it, or contain it, has its
        // own center within one bucket-width of `location`. Deduplicated via
        // the HashSet of keys first, since a location near a bucket boundary
        // can compute the same neighboring key from more than one of the 9
        // (x, y) combinations below.
        private static IEnumerable<XYZ> NearbyGridPoints(XYZ location)
        {
            var bucketSize = BucketSize;
            var xs = new[] { location.X - bucketSize, location.X, location.X + bucketSize };
            var ys = new[] { location.Y - bucketSize, location.Y, location.Y + bucketSize };
            var keys = new HashSet<(int X, int Y)>();

            foreach (var x in xs)
            {
                foreach (var y in ys)
                {
                    keys.Add(BucketKey(new XYZ(x, y, location.Z)));
                }
            }

            foreach (var key in keys)
            {
                if (_gridPointMap.TryGetValue(key, out var bucket))
                {
                    foreach (var point in bucket)
                    {
                        yield return point;
                    }
                }
            }
        }

        // The cell whose bounds actually contain `location` (an AABB
        // containment test, not nearest-by-distance), or null if it's in a
        // gap - next to an obstruction, or outside the grid entirely.
        // NearbyGridPoints(location) is enough to find it: whichever cell
        // contains `location` must have its own center within one
        // bucket-width of it, since no cell is wider than BucketSize.
        private static GridCell? FindContainingCell(XYZ location)
        {
            foreach (var candidate in NearbyGridPoints(location))
            {
                var candidateCell = _cells[CellKey(candidate)];

                if (location.X >= candidateCell.MinXY.X && location.X <= candidateCell.MaxXY.X &&
                    location.Y >= candidateCell.MinXY.Y && location.Y <= candidateCell.MaxXY.Y)
                {
                    return candidateCell;
                }
            }

            return null;
        }

        // Connects every pair of edge- or corner-adjacent cells with a
        // GridConnection on each side (see TryConnect). For each cell,
        // NearbyGridPoints narrows the search to
        // its own spatial-hash bucket and the 8 surrounding it instead of an
        // O(n^2) scan over every cell in the grid. visitedPairs guards
        // against processing the same unordered pair twice - each cell's
        // neighbor search finds the other one back, so without it every
        // connection would get added twice.
        private static void BuildConnections()
        {
            var visitedPairs = new HashSet<((long, long, long) A, (long, long, long) B)>();

            foreach (var cell in _cells.Values)
            {
                var cellKey = CellKey(cell.Location);

                foreach (var neighborPoint in NearbyGridPoints(cell.Location))
                {
                    var neighborKey = CellKey(neighborPoint);

                    if (neighborKey == cellKey ||
                        visitedPairs.Contains((cellKey, neighborKey)) ||
                        visitedPairs.Contains((neighborKey, cellKey)))
                    {
                        continue;
                    }

                    visitedPairs.Add((cellKey, neighborKey));
                    TryConnect(cell, _cells[neighborKey]);
                }
            }
        }

        // Tests all four relative positions a pair of axis-aligned cells
        // could be edge-adjacent in, and wires up both directions' worth of
        // GridConnection the first (and only) one that matches.
        private static void TryConnect(GridCell a, GridCell b)
        {
            if (TryGetSharedEdge(a, b, alongX: true, out var connectLocation))
            {
                Connect(a, b, connectLocation, GridConnectionDirection.E, GridConnectionDirection.W);
            }
            else if (TryGetSharedEdge(b, a, alongX: true, out connectLocation))
            {
                Connect(a, b, connectLocation, GridConnectionDirection.W, GridConnectionDirection.E);
            }
            else if (TryGetSharedEdge(a, b, alongX: false, out connectLocation))
            {
                Connect(a, b, connectLocation, GridConnectionDirection.N, GridConnectionDirection.S);
            }
            else if (TryGetSharedEdge(b, a, alongX: false, out connectLocation))
            {
                Connect(a, b, connectLocation, GridConnectionDirection.S, GridConnectionDirection.N);
            }
            else if (TryGetSharedCorner(a, b, out connectLocation, out var aToB, out var bToA))
            {
                Connect(a, b, connectLocation, aToB, bToA);
            }
        }

        private const double AdjacencyEpsilon = 1e-6;

        // True if `from`'s far edge along `axis` touches `to`'s near edge
        // along the same axis (i.e. `to` sits on `from`'s positive side),
        // with a nonzero-length overlap along the perpendicular axis.
        // connectLocation is the center of whichever of the two cells has
        // the shorter edge along that perpendicular axis - the smaller cell,
        // when the pair is different sizes - placed on the shared boundary.
        private static bool TryGetSharedEdge(GridCell from, GridCell to, bool alongX, out XYZ connectLocation)
        {
            connectLocation = XYZ.Zero;

            var fromFar = alongX ? from.MaxXY.X : from.MaxXY.Y;
            var toNear = alongX ? to.MinXY.X : to.MinXY.Y;

            if (Math.Abs(fromFar - toNear) >= AdjacencyEpsilon)
            {
                return false;
            }

            var fromMinPerp = alongX ? from.MinXY.Y : from.MinXY.X;
            var fromMaxPerp = alongX ? from.MaxXY.Y : from.MaxXY.X;
            var toMinPerp = alongX ? to.MinXY.Y : to.MinXY.X;
            var toMaxPerp = alongX ? to.MaxXY.Y : to.MaxXY.X;

            if (fromMinPerp >= toMaxPerp - AdjacencyEpsilon || fromMaxPerp <= toMinPerp + AdjacencyEpsilon)
            {
                return false;
            }

            var fromLength = fromMaxPerp - fromMinPerp;
            var toLength = toMaxPerp - toMinPerp;
            var useFrom = fromLength <= toLength;
            var connectPerp = useFrom ? (fromMinPerp + fromMaxPerp) / 2.0 : (toMinPerp + toMaxPerp) / 2.0;

            connectLocation = alongX ? new XYZ(fromFar, connectPerp, 0) : new XYZ(connectPerp, fromFar, 0);
            return true;
        }

        // True if `a` and `b` touch at exactly one point - a shared corner,
        // not a shared edge (TryGetSharedEdge already claims any pair that
        // overlaps along the perpendicular axis, so by the time this runs
        // neither cell's edge actually overlaps the other's - they can only
        // be touching at their corners, if at all). Requires both axes to
        // touch at a boundary simultaneously: a's far/near edge lines up
        // with b's near/far edge on X, and independently on Y. connectLocation
        // is that single shared point - there's no "edge" to take a midpoint
        // of the way TryGetSharedEdge does.
        private static bool TryGetSharedCorner(GridCell a, GridCell b, out XYZ connectLocation, out GridConnectionDirection aToB, out GridConnectionDirection bToA)
        {
            connectLocation = XYZ.Zero;
            aToB = GridConnectionDirection.None;
            bToA = GridConnectionDirection.None;

            double sharedX;
            bool bIsEast;

            if (Math.Abs(a.MaxXY.X - b.MinXY.X) < AdjacencyEpsilon)
            {
                sharedX = a.MaxXY.X;
                bIsEast = true;
            }
            else if (Math.Abs(a.MinXY.X - b.MaxXY.X) < AdjacencyEpsilon)
            {
                sharedX = a.MinXY.X;
                bIsEast = false;
            }
            else
            {
                return false;
            }

            double sharedY;
            bool bIsNorth;

            if (Math.Abs(a.MaxXY.Y - b.MinXY.Y) < AdjacencyEpsilon)
            {
                sharedY = a.MaxXY.Y;
                bIsNorth = true;
            }
            else if (Math.Abs(a.MinXY.Y - b.MaxXY.Y) < AdjacencyEpsilon)
            {
                sharedY = a.MinXY.Y;
                bIsNorth = false;
            }
            else
            {
                return false;
            }

            connectLocation = new XYZ(sharedX, sharedY, 0);

            aToB = bIsNorth
                ? (bIsEast ? GridConnectionDirection.NE : GridConnectionDirection.NW)
                : (bIsEast ? GridConnectionDirection.SE : GridConnectionDirection.SW);

            bToA = bIsNorth
                ? (bIsEast ? GridConnectionDirection.SW : GridConnectionDirection.SE)
                : (bIsEast ? GridConnectionDirection.NW : GridConnectionDirection.NE);

            return true;
        }

        private static void Connect(GridCell a, GridCell b, XYZ connectLocation, GridConnectionDirection aToB, GridConnectionDirection bToA)
        {
            a.Connections.Add(new GridConnection(connectLocation, b.Location, aToB));
            b.Connections.Add(new GridConnection(connectLocation, a.Location, bToA));
        }

        // Subdivides one coarse cell's footprint (min/max) into a square
        // grid of FineResolution cells - (GridResolution / FineResolution)
        // on a side, always a whole number since FineResolution is always a
        // whole divisor of GridResolution (see its getter) - keeping only
        // the ones that don't overlap an obstruction. The overhang clip
        // below is now just a defensive backstop against floating-point
        // rounding at the last step, not a real divisibility gap.
        private static int AddFineCells(XYZ boundsMin, XYZ boundsMax)
        {
            var fineResolution = FineResolution;

            if (fineResolution <= 0)
            {
                return 0;
            }

            var fineHalfCell = fineResolution / 2.0;
            var added = 0;

            for (var fx = boundsMin.X + fineHalfCell; fx <= boundsMax.X; fx += fineResolution)
            {
                for (var fy = boundsMin.Y + fineHalfCell; fy <= boundsMax.Y; fy += fineResolution)
                {
                    var fineMin = new XYZ(fx - fineHalfCell, fy - fineHalfCell, 0);
                    var fineMax = new XYZ(fx + fineHalfCell, fy + fineHalfCell, 0);

                    if (fineMin.X < boundsMin.X || fineMax.X > boundsMax.X ||
                        fineMin.Y < boundsMin.Y || fineMax.Y > boundsMax.Y)
                    {
                        continue;
                    }

                    var fineCell = new GridCell(fineMin, fineMax);

                    if (OverlapsAnyObstruction(fineCell))
                    {
                        continue;
                    }

                    var finePoint = new XYZ(fx, fy, 0);
                    AddGridPoint(finePoint);
                    _cells[CellKey(finePoint)] = fineCell;
                    added++;
                }
            }

            return added;
        }

        private static (long X, long Y, long Z) CellKey(XYZ location) =>
            ((long)Math.Round(location.X * 1000), (long)Math.Round(location.Y * 1000), (long)Math.Round(location.Z * 1000));

        private static XYZ LocationFromCellKey((long X, long Y, long Z) key) =>
            new(key.X / 1000.0, key.Y / 1000.0, key.Z / 1000.0);

        // Deterministic DebugDraw id from a cell's whole-number location,
        // rather than tracking ids returned by DebugDraw.Rectangle in a
        // parallel list. CoordinateOffset shifts both axes positive first -
        // our grid's X/Y (-150..150 / -50..50) comfortably clears the 16-bit
        // half each axis gets, so the two halves never collide with each
        // other, and the packed result is large enough to never collide with
        // the small sequential ids DebugDraw hands out elsewhere (NextId()
        // starts at 1). ObstructionIdFlag distinguishes an obstruction's id
        // from a grid cell's at the same location - both are now drawn
        // immediately and can coexist on screen, so without this an
        // obstruction placed exactly on a grid point would silently replace
        // that cell's DebugDraw record instead of adding its own.
        private const int CoordinateOffset = 1024;
        private const int ObstructionIdFlag = 1 << 30;

        private static int CellDrawId(XYZ location, bool isObstruction = false)
        {
            var packed = ((int)Math.Round(location.X) + CoordinateOffset) << 16 | ((int)Math.Round(location.Y) + CoordinateOffset);
            return isObstruction ? packed | ObstructionIdFlag : packed;
        }

        [Schedulable("DrawGrid")]
        public static bool DrawGrid()
        {
            if (_gridPointCount == 0)
            {
                Logger.Log("DrawGrid: no grid points - call CreateGrid first.");
                return false;
            }

            DebugOverlay.ClearOverlay();
            _highlightedKey = null;
            _highlightedColor = null;

            if (_showConnections)
            {
                DrawConnections();
            }
            else
            {
                DrawCells();
                DrawPath();
            }

            foreach (var (key, obstruction) in _obstructions)
            {
                DebugDraw.Rectangle(CellDrawId(LocationFromCellKey(key), isObstruction: true), obstruction.MinXY, obstruction.MaxXY, Colors.Red, filled: true);
            }

            // Drawn regardless of _showConnections, same as obstructions -
            // these mark the pathfinding endpoints, not the grid's cells.
            var markerRadius = FineResolution / 3.0;

            if (_pathStart != null)
            {
                DebugDraw.Circle(null, _pathStart, markerRadius, Colors.Green);
            }

            if (_pathEnd != null)
            {
                DebugDraw.Circle(null, _pathEnd, markerRadius, Colors.Purple);
            }

            return true;
        }

        private static void DrawCells()
        {
            foreach (var point in AllGridPoints())
            {
                var cell = _cells[CellKey(point)];
                DebugDraw.Rectangle(CellDrawId(point), cell.MinXY, cell.MaxXY, Colors.Cyan);
            }
        }

        // A small marker at every connection's _connectLocation (the
        // shared-edge midpoint, not either cell's own center). Drawn per
        // connection rather than deduplicated per pair - both directions of
        // a pair share the same _connectLocation, so this draws the same
        // circle twice (once from each side), which just harmlessly
        // overlaps rather than hiding anything.
        private static void DrawConnections()
        {
            var radius = FineResolution / 4.0;

            foreach (var cell in _cells.Values)
            {
                foreach (var connection in cell.Connections)
                {
                    DebugDraw.Circle(null, connection._connectLocation, radius, Colors.Blue);
                }
            }
        }

        // Called from DebugOverlay.UpdateOverlay every Idling tick - not
        // itself Schedulable, since nothing needs to invoke it by name.
        // Recolors only the two rectangles that actually changed (old
        // highlight back to cyan, new one to blue) rather than redrawing
        // the whole grid, so this stays cheap even at high tick rates.
        //
        // "Nearest" means containing, not closest-by-distance - the
        // highlighted cell is whichever one's bounds the mouse is actually
        // inside. If the mouse isn't over any cell (e.g. it's in a gap next
        // to an obstruction, or outside the grid entirely), nothing is
        // highlighted. NearbyGridPoints(worldPoint) is enough to find it:
        // every cell is at most GridResolution (= BucketSize) wide, so
        // whichever cell contains worldPoint must have its own center
        // within one bucket-width of it.
        public static void UpdateHighlight(UIApplication uiApp)
        {
            if (_gridPointCount == 0 || _showConnections)
            {
                return;
            }

            if (!TryResolveActiveView(uiApp, out var view, out var uiView))
            {
                return;
            }

            var mouseScreenPoint = Win32Interop.GetCursorPosition();

            if (!ViewProjector.TryUnprojectPlanView(view, uiView, mouseScreenPoint, out var worldPoint))
            {
                return;
            }

            var containingCellFound = FindContainingCell(worldPoint);
            (long X, long Y, long Z)? containingKey = containingCellFound == null ? null : CellKey(containingCellFound.Location);

            if (containingKey == null)
            {
                if (_highlightedKey.HasValue)
                {
                    var previousCell = _cells[_highlightedKey.Value];
                    DebugDraw.Rectangle(CellDrawId(LocationFromCellKey(_highlightedKey.Value)), previousCell.MinXY, previousCell.MaxXY, Colors.Cyan);
                    _highlightedKey = null;
                    _highlightedColor = null;
                }

                return;
            }

            // Red while arming an obstruction placement, so the target cell
            // reads as "this is what gets blocked" rather than the normal
            // hover color. Compared against _highlightedColor (not just the
            // key) so pressing "Add Obstruction" while the mouse is
            // stationary still flips the already-highlighted cell to red
            // immediately, instead of waiting for the mouse to move.
            var highlightColor = _waitingForObstructionClick ? Colors.Red : Colors.Blue;

            if (_highlightedKey == containingKey && _highlightedColor == highlightColor)
            {
                return;
            }

            if (_highlightedKey.HasValue && _highlightedKey != containingKey)
            {
                var previousCell = _cells[_highlightedKey.Value];
                DebugDraw.Rectangle(CellDrawId(LocationFromCellKey(_highlightedKey.Value)), previousCell.MinXY, previousCell.MaxXY, Colors.Cyan);
            }

            var containingCell = _cells[containingKey.Value];
            DebugDraw.Rectangle(CellDrawId(LocationFromCellKey(containingKey.Value)), containingCell.MinXY, containingCell.MaxXY, highlightColor);
            _highlightedKey = containingKey;
            _highlightedColor = highlightColor;
        }

        // Called from DebugOverlay.UpdateOverlay every Idling tick, same as
        // UpdateHighlight. Left-button state is polled directly (the overlay
        // window is click-through, so it never receives mouse events) - a
        // click only counts if it lands inside the active view's viewport,
        // so clicking the panel itself (e.g. one of the arming buttons)
        // can never register as the placement click. Dispatches to whichever
        // of AddObstruction/PathStart/PathEnd is currently armed - only one
        // click-detection pass runs per tick, since _wasLeftButtonDown is
        // shared state that can only report a fresh press once per tick.
        public static void UpdateClickPlacement(UIApplication uiApp)
        {
            var isLeftButtonDown = Win32Interop.IsLeftButtonDown();
            var justPressed = isLeftButtonDown && !_wasLeftButtonDown;
            _wasLeftButtonDown = isLeftButtonDown;

            if (!justPressed ||
                (!_waitingForObstructionClick && !_waitingForPathStartClick && !_waitingForPathEndClick) ||
                !Win32Interop.IsForegroundProcess())
            {
                return;
            }

            if (!TryResolveActiveView(uiApp, out var view, out var uiView))
            {
                return;
            }

            var mouseScreenPoint = Win32Interop.GetCursorPosition();
            var rect = uiView.GetWindowRectangle();

            if (mouseScreenPoint.X < rect.Left || mouseScreenPoint.X > rect.Right ||
                mouseScreenPoint.Y < rect.Top || mouseScreenPoint.Y > rect.Bottom)
            {
                return;
            }

            if (!ViewProjector.TryUnprojectPlanView(view, uiView, mouseScreenPoint, out var worldPoint))
            {
                return;
            }

            if (_waitingForObstructionClick)
            {
                PlaceObstruction(worldPoint);
            }
            else if (_waitingForPathStartClick)
            {
                PlacePathPoint(worldPoint, isStart: true);
            }
            else if (_waitingForPathEndClick)
            {
                PlacePathPoint(worldPoint, isStart: false);
            }
        }

        private static void PlaceObstruction(XYZ worldPoint)
        {
            var location = new XYZ(Math.Round(worldPoint.X), Math.Round(worldPoint.Y), 0);

            // Integer division rather than /2.0 - keeps min/max on whole
            // numbers even for an odd obstruction size, at the cost of the
            // cell being offset by one unit rather than perfectly centered.
            var halfSize = _obstructionSize / 2;
            var min = new XYZ(location.X - halfSize, location.Y - halfSize, 0);
            var max = new XYZ(min.X + _obstructionSize, min.Y + _obstructionSize, 0);
            var cell = new GridCell(min, max);
            _obstructions[CellKey(location)] = cell;
            _waitingForObstructionClick = false;
            OverlayState.IsAddingObstruction = false;

            Logger.Log($"AddObstruction: placed a {_obstructionSize}\" obstruction at ({location.X:F0}, {location.Y:F0}).");

            // Full regenerate rather than just removing overlapping cells -
            // that alone would leave a coarse-sized hole around the new
            // obstruction instead of backfilling it with fine cells, same as
            // CreateGrid already does when an obstruction exists beforehand.
            // _cells.Count == 0 guards the case where a "Create Grid" was
            // never actually run yet - placing an obstruction shouldn't
            // silently build a grid the user never asked for.
            if (_cells.Count > 0 || _gridPointCount > 0)
            {
                CreateGrid();
            }
        }

        // The click has to land inside an actual cell - the cached location
        // is a grid-cell center, not an arbitrary point, so a future
        // pathfinding pass across the connection graph can start/end there.
        // A miss (obstruction, gap, outside the grid) logs and leaves the
        // arming flag set so the next click can try again.
        private static void PlacePathPoint(XYZ worldPoint, bool isStart)
        {
            var label = isStart ? "PathStart" : "PathEnd";
            var containingCell = FindContainingCell(worldPoint);

            if (containingCell == null)
            {
                Logger.Log($"{label}: click landed outside any grid cell - try again.");
                return;
            }

            // Whichever endpoint just moved invalidates any previously found
            // path (or failure indicator) - clear both now so nothing stale
            // lingers on screen past this click.
            _path.Clear();
            _pathSearchFailed = false;

            if (isStart)
            {
                _pathStart = containingCell.Location;
                _waitingForPathStartClick = false;
                OverlayState.IsSettingPathStart = false;
            }
            else
            {
                _pathEnd = containingCell.Location;
                _waitingForPathEndClick = false;
                OverlayState.IsSettingPathEnd = false;
            }

            Logger.Log($"{label}: set to ({containingCell.Location.X:F0}, {containingCell.Location.Y:F0}).");

            // Setting PathEnd is the natural "go" trigger, since both
            // endpoints are only ever complete once this one lands. FindPath
            // draws the result itself on success; on failure (including
            // PathStart not being set yet) we still need a redraw so the
            // just-cleared _path and the moved marker actually show up.
            var pathFound = !isStart && FindPath();

            if (!pathFound)
            {
                DrawGrid();
            }
        }

        // A*: validates PathStart/PathEnd, resolves their containing cells,
        // then repeatedly pops the lowest-TotalCost node and expands it via
        // TryCreateSuccessor until one popped node is actually at the goal
        // cell. The start node's _connectDirection is None - it arrived
        // from nowhere, so it has nothing to double back on and no turn
        // penalty (see TryCreateSuccessor).
        //
        // visited is a closed set keyed by exactly the three things that
        // make a search state unique (StateKey): cell, arrival direction,
        // and distance since the last turn. Since every edge cost is
        // non-negative, the first time a given state is popped it's already
        // at its lowest possible cost - later duplicate entries for that
        // same state (PriorityQueue has no decrease-key, so a cheaper
        // rediscovery is just enqueued again rather than updated in place)
        // are safely skipped instead of re-expanded.
        [Schedulable("FindPath")]
        public static bool FindPath()
        {
            if (_pathStart == null || _pathEnd == null)
            {
                Logger.Log("FindPath: set both PathStart and PathEnd first.");
                return false;
            }

            var startCell = FindContainingCell(_pathStart);
            var endCell = FindContainingCell(_pathEnd);

            if (startCell == null || endCell == null)
            {
                Logger.Log("FindPath: PathStart or PathEnd is no longer inside a grid cell - set them again.");
                return false;
            }

            if (ReferenceEquals(startCell, endCell))
            {
                Logger.Log("FindPath: PathStart and PathEnd are the same cell.");
                return false;
            }

            var heap = new PriorityQueue<GridSearchNode, double>();
            var startNode = new GridSearchNode(startCell, null, null, GridConnectionDirection.None, 0, startCell.Location.DistanceTo(endCell.Location), 0, 0);
            heap.Enqueue(startNode, startNode.TotalCost);

            var visited = new HashSet<(long CellX, long CellY, long CellZ, GridConnectionDirection Direction, long DistanceFromLastTurn)>();
            var endCellKey = CellKey(endCell.Location);

            while (heap.Count > 0)
            {
                var current = heap.Dequeue();

                if (!visited.Add(StateKey(current)))
                {
                    continue;
                }

                if (CellKey(current._cell.Location) == endCellKey)
                {
                    var pathNodes = ReconstructPath(current);
                    Logger.Log($"FindPath: found a path with {pathNodes.Count} cell(s), {current._turnCount} turn(s), cost {current._costFromStart:F1}.");

                    _path.Clear();
                    _pathSearchFailed = false;

                    foreach (var node in pathNodes)
                    {
                        if (node._connection != null)
                        {
                            _path.Add(node._connection);
                        }
                    }

                    DrawGrid();
                    return true;
                }

                foreach (var connection in current._cell.Connections)
                {
                    var successor = TryCreateSuccessor(current, connection);

                    if (successor != null && !visited.Contains(StateKey(successor)))
                    {
                        heap.Enqueue(successor, successor.TotalCost);
                    }
                }
            }

            Logger.Log("FindPath: no path found.");
            _pathSearchFailed = true;
            DrawGrid();
            return false;
        }

        // See FindPath's comment on `visited` - distanceFromLastTurn is
        // rounded the same way CellKey rounds a location, so two states that
        // are the same up to floating-point noise still key as equal.
        private static (long CellX, long CellY, long CellZ, GridConnectionDirection Direction, long DistanceFromLastTurn) StateKey(GridSearchNode node)
        {
            var cellKey = CellKey(node._cell.Location);
            return (cellKey.X, cellKey.Y, cellKey.Z, node._connectDirection, (long)Math.Round(node._distanceFromLastTurn * 1000));
        }

        private static List<GridSearchNode> ReconstructPath(GridSearchNode goalNode)
        {
            var path = new List<GridSearchNode>();
            var node = goalNode;

            while (node != null)
            {
                path.Add(node);
                node = node._parent;
            }

            path.Reverse();
            return path;
        }

        // Draws the cached _path (called only from DrawGrid's non-Connections
        // branch) as a right-angle polyline: origin -> first connectLocation
        // -> first neighborLocation (the cell just entered, i.e. the
        // "mid-point") -> next connectLocation -> next neighborLocation ->
        // ... Routing every hop through each cell's own center rather than
        // straight between consecutive connectLocations is what produces a
        // proper right-angle elbow at a turn, instead of cutting the corner
        // diagonally. The last neighborLocation lands exactly on _pathEnd.
        // Green lines for the segments; blue circles at every point except
        // the origin (already marked by its own green PathStart circle) -
        // that covers both each connectLocation and each intermediate
        // cell-center.
        private static void DrawPath()
        {
            if (_pathSearchFailed && _pathStart != null && _pathEnd != null)
            {
                DebugDraw.Line(null, _pathStart, _pathEnd, Colors.Red, thickness: 3);
                return;
            }

            if (_path.Count == 0 || _pathStart == null)
            {
                return;
            }

            var points = new List<XYZ> { _pathStart };

            foreach (var connection in _path)
            {
                points.Add(connection._connectLocation);
                points.Add(connection._neighborLocation);
            }

            for (var i = 0; i < points.Count - 1; i++)
            {
                DebugDraw.Line(null, points[i], points[i + 1], Colors.Green, thickness: 3);
            }

            var markerRadius = FineResolution / 4.0;

            for (var i = 1; i < points.Count; i++)
            {
                DebugDraw.Circle(null, points[i], markerRadius, Colors.Blue);
            }
        }

        // Compass bearing in degrees, clockwise from N - used to measure how
        // sharp a turn between two directions actually is.
        private static readonly Dictionary<GridConnectionDirection, int> DirectionBearings = new()
        {
            { GridConnectionDirection.N, 0 },
            { GridConnectionDirection.NE, 45 },
            { GridConnectionDirection.E, 90 },
            { GridConnectionDirection.SE, 135 },
            { GridConnectionDirection.S, 180 },
            { GridConnectionDirection.SW, 225 },
            { GridConnectionDirection.W, 270 },
            { GridConnectionDirection.NW, 315 }
        };

        // A turn is only permitted if it's 90 degrees or less - e.g. from N,
        // only N/NE/E/W/NW are reachable; S, SE, and SW all require sharper
        // turns than that and are disallowed, not just the exact 180-degree
        // opposite (S). Covers the diagonals too, even though TryConnect
        // can't produce them yet, so this stays correct once something does.
        private static bool IsDisallowedTurn(GridConnectionDirection from, GridConnectionDirection to)
        {
            var fromBearing = DirectionBearings[from];
            var toBearing = DirectionBearings[to];
            var difference = Math.Abs(fromBearing - toBearing);
            difference = Math.Min(difference, 360 - difference);

            return difference > 90;
        }

        // Builds the node reached by following `connection` away from
        // `current` - null if that's not permitted:
        //   - Any turn sharper than 90 degrees off the direction `current`
        //     arrived from is never allowed (see IsDisallowedTurn) - not
        //     just the exact 180-degree double-back.
        //   - A turn of 90 degrees or less (leaving in a direction other
        //     than the one `current` arrived from) is only allowed once
        //     current._distanceFromLastTurn has reached TurnMinDistance -
        //     otherwise we're locked into continuing straight until that
        //     minimum run is satisfied.
        // The start node (_connectDirection None) has no incoming direction
        // to compare against, so its first move is never a turn and is
        // never gated by TurnMinDistance.
        //
        // Turning also adds a full GridResolution to _costFromStart (on top
        // of the actual step distance) - since A* always expands the lowest
        // TotalCost node next, and the heuristic (straight-line distance,
        // which ignores turn penalties entirely) still never overestimates
        // the true remaining cost, this keeps the search optimal while
        // biasing it toward whichever path racks up the fewest such
        // penalties - i.e. the fewest turns - among paths of comparable
        // physical length. A path that turns more can still win if its
        // extra turn buys it a large enough distance saving to outweigh the
        // GridResolution penalty, which is the intended tradeoff rather than
        // an outright ban on turning.
        private static GridSearchNode? TryCreateSuccessor(GridSearchNode current, GridConnection connection)
        {
            var direction = connection._connectionDirection;
            var isFirstMove = current._connectDirection == GridConnectionDirection.None;

            if (!isFirstMove && IsDisallowedTurn(current._connectDirection, direction))
            {
                return null;
            }

            var isTurn = !isFirstMove && direction != current._connectDirection;

            if (isTurn && current._distanceFromLastTurn < _turnMinDistance)
            {
                return null;
            }

            var neighborCell = _cells[CellKey(connection._neighborLocation)];
            var stepCost = current._cell.Location.DistanceTo(neighborCell.Location);
            var turnCount = current._turnCount + (isTurn ? 1 : 0);
            var costFromStart = current._costFromStart + stepCost + (isTurn ? _gridResolution : 0);
            var estimateToDestination = _pathEnd == null ? 0 : neighborCell.Location.DistanceTo(_pathEnd);
            var distanceFromLastTurn = isFirstMove || isTurn ? stepCost : current._distanceFromLastTurn + stepCost;

            return new GridSearchNode(neighborCell, current, connection, direction, costFromStart, estimateToDestination, turnCount, distanceFromLastTurn);
        }

        // Standard AABB overlap test - cells that merely touch edges (no
        // shared interior area) don't count as overlapping.
        private static bool CellsOverlap(GridCell a, GridCell b) =>
            a.MinXY.X < b.MaxXY.X && a.MaxXY.X > b.MinXY.X &&
            a.MinXY.Y < b.MaxXY.Y && a.MaxXY.Y > b.MinXY.Y;

        private static bool OverlapsAnyObstruction(GridCell cell) =>
            _obstructions.Values.Any(obstruction => CellsOverlap(cell, obstruction));

        // Overlap against each obstruction's bounds inflated by
        // bufferDistance on every side - used to decide coarse vs. fine at
        // the macro-cell level, so a whole GridResolution-wide margin around
        // every obstruction falls back to fine cells, not just the cells an
        // obstruction literally touches. Individual fine cells still use the
        // unbuffered OverlapsAnyObstruction, so fine coverage still reaches
        // all the way to the obstruction's actual edge.
        private static bool IsNearAnyObstruction(GridCell cell, double bufferDistance)
        {
            foreach (var obstruction in _obstructions.Values)
            {
                var inflated = new GridCell(
                    new XYZ(obstruction.MinXY.X - bufferDistance, obstruction.MinXY.Y - bufferDistance, 0),
                    new XYZ(obstruction.MaxXY.X + bufferDistance, obstruction.MaxXY.Y + bufferDistance, 0));

                if (CellsOverlap(cell, inflated))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveActiveView(UIApplication uiApp, out View view, out UIView uiView)
        {
            view = null!;
            uiView = null!;

            var uiDoc = uiApp.ActiveUIDocument;

            if (uiDoc == null)
            {
                return false;
            }

            var activeView = uiDoc.ActiveView;
            var found = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == activeView.Id);
            view = activeView;

            if (found == null)
            {
                return false;
            }

            uiView = found;
            return true;
        }
    }
}
