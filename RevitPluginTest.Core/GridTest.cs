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

        // Armed by the "Add Obstruction" button; consumed by the next fresh
        // left-click detected in UpdateObstructionPlacement. _wasLeftButtonDown
        // tracks the button's state from the previous tick so a held-down
        // click (e.g. the one that pressed the button itself) doesn't
        // re-trigger - only a 0->1 transition counts.
        private static bool _waitingForObstructionClick;
        private static bool _wasLeftButtonDown;

        // Toggled by the "Connections" checkbox - swaps DrawGrid between its
        // normal cell rectangles and a view of just the connection graph.
        // Obstructions are drawn either way (see DrawGrid).
        private static bool _showConnections;

        public static int GridResolution => _gridResolution;

        // No longer independently settable - always exactly a quarter of
        // GridResolution, so it divides GridResolution evenly by
        // construction (4 fine cells per coarse cell per axis). A
        // user-chosen FineResolution that didn't evenly divide GridResolution
        // was the actual cause of the gaps between fine cells - the clip in
        // AddFineCells prevented overlaps, but left slivers wherever the
        // division wasn't exact. GridResolution is itself constrained to
        // multiples of 4 (see SetGridResolution), so this integer division
        // is always exact - never truncates a fraction away.
        public static int FineResolution => _gridResolution / 4;

        public static int ObstructionSize => _obstructionSize;
        public static bool ShowConnections => _showConnections;

        // The panel slider reports a raw double - snapped to the nearest
        // (nonzero) multiple of 4 here rather than relying on the slider to
        // snap, so GridResolution / FineResolution == 4 always holds exactly.
        [Schedulable("SetGridResolution", Quiet = true)]
        public static bool SetGridResolution(double resolution)
        {
            var nearestMultipleOf4 = (int)Math.Round(resolution / 4.0) * 4;
            _gridResolution = Math.Max(nearestMultipleOf4, 4);
            return true;
        }

        [Schedulable("SetObstructionSize", Quiet = true)]
        public static bool SetObstructionSize(double size)
        {
            _obstructionSize = (int)Math.Round(size);
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
        // UpdateObstructionPlacement (every Idling tick) places the
        // obstruction and disarms.
        [Schedulable("AddObstruction")]
        public static bool AddObstruction()
        {
            _waitingForObstructionClick = true;
            OverlayState.IsAddingObstruction = true;
            Logger.Log("AddObstruction: click a location in the view to place an obstruction.");
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
        // with every neighboring coarse cell, and FineResolution is always
        // exactly a quarter of GridResolution (see the FineResolution
        // property) - so it divides evenly by construction and fine cells
        // tile seamlessly against both fine and coarse neighbors with no
        // seam gaps. The only gaps left are where a cell would actually
        // overlap the obstruction.
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
        // way, since the largest cell is exactly 2*BucketSize wide, so
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

        // Connects every pair of edge-adjacent cells with a GridConnection on
        // each side. For each cell, NearbyGridPoints narrows the search to
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
                Connect(a, b, connectLocation, GridConnectionDirection.Right, GridConnectionDirection.Left);
            }
            else if (TryGetSharedEdge(b, a, alongX: true, out connectLocation))
            {
                Connect(a, b, connectLocation, GridConnectionDirection.Left, GridConnectionDirection.Right);
            }
            else if (TryGetSharedEdge(a, b, alongX: false, out connectLocation))
            {
                Connect(a, b, connectLocation, GridConnectionDirection.Up, GridConnectionDirection.Down);
            }
            else if (TryGetSharedEdge(b, a, alongX: false, out connectLocation))
            {
                Connect(a, b, connectLocation, GridConnectionDirection.Down, GridConnectionDirection.Up);
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

        private static void Connect(GridCell a, GridCell b, XYZ connectLocation, GridConnectionDirection aToB, GridConnectionDirection bToA)
        {
            a.Connections.Add(new GridConnection(connectLocation, b.Location, aToB));
            b.Connections.Add(new GridConnection(connectLocation, a.Location, bToA));
        }

        // Subdivides one coarse cell's footprint (min/max) into FineResolution
        // cells (always exactly 4x4 = 16, since FineResolution is fixed at a
        // quarter of GridResolution), keeping only the ones that don't
        // overlap an obstruction. The overhang clip below is now just a
        // defensive backstop against floating-point rounding at the last
        // step, not a real divisibility gap - FineResolution * 4 ==
        // GridResolution exactly, by construction.
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
            }

            foreach (var (key, obstruction) in _obstructions)
            {
                DebugDraw.Rectangle(CellDrawId(LocationFromCellKey(key), isObstruction: true), obstruction.MinXY, obstruction.MaxXY, Colors.Red, filled: true);
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

            (long X, long Y, long Z)? containingKey = null;

            foreach (var candidate in NearbyGridPoints(worldPoint))
            {
                var candidateKey = CellKey(candidate);
                var candidateCell = _cells[candidateKey];

                if (worldPoint.X >= candidateCell.MinXY.X && worldPoint.X <= candidateCell.MaxXY.X &&
                    worldPoint.Y >= candidateCell.MinXY.Y && worldPoint.Y <= candidateCell.MaxXY.Y)
                {
                    containingKey = candidateKey;
                    break;
                }
            }

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
        // so clicking the panel itself (e.g. the "Add Obstruction" button)
        // can never register as the placement click.
        public static void UpdateObstructionPlacement(UIApplication uiApp)
        {
            var isLeftButtonDown = Win32Interop.IsLeftButtonDown();
            var justPressed = isLeftButtonDown && !_wasLeftButtonDown;
            _wasLeftButtonDown = isLeftButtonDown;

            if (!_waitingForObstructionClick || !justPressed || !Win32Interop.IsForegroundProcess())
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
