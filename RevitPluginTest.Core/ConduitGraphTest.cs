using System.Linq;
using Autodesk.Revit.DB;
using Colors = System.Windows.Media.Colors;

namespace RevitPluginTest.Core
{
    public struct tNode
    {
        public int ID;
        public XYZ Location;
        public bool IsSource;

        public tNode(int inId, XYZ inLocation, bool inIsSource)
        {
            ID = inId;
            Location = inLocation;
            IsSource = inIsSource;
        }
    }

    // A candidate conduit route to one source: an ordered list of waypoints,
    // right-angle turns only, from the receptacle up to routing height,
    // across to above the source, and down into it.
    public readonly struct tConduitPath
    {
        public int SourceId { get; }
        public double Length { get; }
        public IReadOnlyList<XYZ> Waypoints { get; }

        // One entry per segment (Waypoints.Count - 1): how many conduits
        // are bundled into that segment. 1 means an ordinary, unshared
        // segment; 2+ means a shared trunk from GraphTestFunctions.MergeParallelRuns,
        // drawn as that many parallel lines instead of a single one. All 1s
        // for a freshly routed (not yet merged) path.
        public IReadOnlyList<int> MergedSegments { get; }

        public tConduitPath(int inSourceId, double inLength, IReadOnlyList<XYZ> inWaypoints, IReadOnlyList<int>? inMergedSegments = null)
        {
            SourceId = inSourceId;
            Length = inLength;
            Waypoints = inWaypoints;
            MergedSegments = inMergedSegments ?? Enumerable.Repeat(1, Math.Max(0, inWaypoints.Count - 1)).ToArray();
        }
    }

    // A physical bundle of conduits sharing one straight trunk line, built
    // by GraphTestFunctions.MergeParallelRuns. Count is always exactly
    // MemberNodeIds.Count - the true, final membership - not a running
    // total accumulated during clustering, since every member is recorded
    // explicitly here rather than each path keeping its own local count.
    public sealed class tConduitRun
    {
        public XYZ Start { get; }
        public XYZ End { get; }
        public IReadOnlyList<int> MemberNodeIds { get; }
        public int Count => MemberNodeIds.Count;

        public tConduitRun(XYZ inStart, XYZ inEnd, IReadOnlyList<int> inMemberNodeIds)
        {
            Start = inStart;
            End = inEnd;
            MemberNodeIds = inMemberNodeIds;
        }
    }

    // The fully solved graph, built once BuildGraph finishes routing and
    // merging: every source and receptacle node, every routing corner that
    // isn't already covered by a conduit run's own start/end, every conduit
    // run, and each non-source node's complete routed path (waypoints +
    // per-segment merge count) - everything DrawGraph needs, so it never
    // has to reach past this into other internal state.
    public sealed class tGraphSolution
    {
        public IReadOnlyDictionary<int, tNode> SourceNodes { get; }
        public IReadOnlyDictionary<int, tNode> NonSourceNodes { get; }
        public IReadOnlyList<XYZ> IntermediatePoints { get; }
        public IReadOnlyList<tConduitRun> ConduitRuns { get; }
        public IReadOnlyDictionary<int, tConduitPath> NonSourcePaths { get; }

        public tGraphSolution(
            IReadOnlyDictionary<int, tNode> inSourceNodes,
            IReadOnlyDictionary<int, tNode> inNonSourceNodes,
            IReadOnlyList<XYZ> inIntermediatePoints,
            IReadOnlyList<tConduitRun> inConduitRuns,
            IReadOnlyDictionary<int, tConduitPath> inNonSourcePaths)
        {
            SourceNodes = inSourceNodes;
            NonSourceNodes = inNonSourceNodes;
            IntermediatePoints = inIntermediatePoints;
            ConduitRuns = inConduitRuns;
            NonSourcePaths = inNonSourcePaths;
        }
    }

    public class ConduitGraphTest
    {
        private readonly Dictionary<int, tNode> _graphNodeMap = new();
        private readonly Dictionary<int, HashSet<int>> _neighborMap = new();

        // Right-angle route geometry for edges built by BuildGraph,
        // keyed by the non-source node's id (each has exactly one). DrawGraph
        // uses this to render the actual routed path instead of a straight
        // arrow; edges from other strategies (Circuits, complete mesh) have
        // no entry here and fall back to a straight arrow.
        private readonly Dictionary<int, tConduitPath> _conduitPaths = new();

        public IEnumerable<tNode> Nodes => _graphNodeMap.Values;

        // Keyed by the non-source node id, same as SetConduitPath.
        public IReadOnlyDictionary<int, tConduitPath> ConduitPaths => _conduitPaths;

        public tGraphSolution? Solution { get; private set; }

        public void SetSolution(tGraphSolution inSolution)
        {
            Solution = inSolution;
        }

        public bool AddNode(in tNode inNode)
        {
            if (_graphNodeMap.ContainsKey(inNode.ID))
            {
                return false;
            }

            _graphNodeMap[inNode.ID] = inNode;
            _neighborMap[inNode.ID] = new HashSet<int>();
            return true;
        }

        public bool TryGetNode(int inId, out tNode outNode) => _graphNodeMap.TryGetValue(inId, out outNode);

        public bool RemoveNode(int inId)
        {
            var removed = _graphNodeMap.Remove(inId);
            _neighborMap.Remove(inId);
            _conduitPaths.Remove(inId);
            return removed;
        }

        // Drops every node matching inIsSource (and, via RemoveNode, that
        // node's own neighbor entry) - AddSources/AddReceptacles call this
        // first so re-adding replaces the prior batch of that type rather
        // than accumulating alongside it.
        public void RemoveNodesByType(bool inIsSource)
        {
            var idsToRemove = _graphNodeMap.Values
                .Where(node => node.IsSource == inIsSource)
                .Select(node => node.ID)
                .ToList();

            foreach (var id in idsToRemove)
            {
                RemoveNode(id);
            }
        }

        public void ClearGraph()
        {
            _graphNodeMap.Clear();
            _neighborMap.Clear();
            _conduitPaths.Clear();
            Solution = null;
        }

        // Keeps nodes, drops edges - each BuildXGraph strategy calls this
        // before repopulating, so switching graph types doesn't leave stale
        // edges from whichever strategy ran previously.
        public void ClearNeighbors()
        {
            foreach (var neighbors in _neighborMap.Values)
            {
                neighbors.Clear();
            }

            _conduitPaths.Clear();
            Solution = null;
        }

        // False if inFromId isn't a known node, or inToId was already a neighbor.
        public bool AddNeighbor(int inFromId, int inToId)
        {
            return _neighborMap.TryGetValue(inFromId, out var neighbors) && neighbors.Add(inToId);
        }

        // Records the actual routed geometry for the edge into inNonSourceId,
        // so DrawGraph can render the real right-angle path instead of a
        // straight arrow. BuildGraph calls this right after AddNeighbor.
        public void SetConduitPath(int inNonSourceId, tConduitPath inPath)
        {
            _conduitPaths[inNonSourceId] = inPath;
        }

        // Connects two wall-perpendicular directions with right-angle turns
        // only, at the shared inRoutingHeight plane, GUARANTEEING the first
        // segment leaves inFrom along inFromDirection and the last segment
        // arrives at inTo along -inToDirection - by construction, not by
        // checking a raw line-intersection's sign afterward (that was the
        // original bug: the naive intersection point can legitimately land
        // behind one of the two nodes, making that end's segment travel
        // backward - toward the exterior - even though its direction was
        // correctly resolved as interior-facing).
        //
        // j1 = a short forced stub off inFrom, along inFromDirection.
        // p2 = a short forced stub off inTo, along inToDirection (so the
        //      final segment p2->inTo is unconditionally along -inToDirection).
        // Getting from j1 to p2 is a second problem the endpoint stubs alone
        // don't solve: a straight geometric connector between them has no
        // idea where the walls or the exterior actually are, so it can cut
        // straight through them even when both endpoints individually face
        // the interior. inCrossroads are candidate waypoints already
        // verified to sit inside a room (see GraphTestFunctions.BuildCrossroadPool);
        // j1 and p2 are connected by the shortest sequence of hops through
        // that pool where each hop shares an X or Y coordinate with the
        // next (so every turn is a right angle, assuming node directions
        // are cardinal - the same assumption the crossroad pool itself
        // makes). Returns null if no such route exists, so the caller can
        // skip this candidate rather than fall back to a route that might
        // cut through the exterior.
        public static List<XYZ>? RouteBetween(XYZ inFrom, XYZ inFromDirection, XYZ inTo, XYZ inToDirection, double inRoutingHeight, IReadOnlyList<XYZ> inCrossroads)
        {
            const double stubLength = 1.0;
            const double axisTolerance = 1e-6;

            var j1 = new XYZ(inFrom.X + stubLength * inFromDirection.X, inFrom.Y + stubLength * inFromDirection.Y, inRoutingHeight);
            var p2 = new XYZ(inTo.X + stubLength * inToDirection.X, inTo.Y + stubLength * inToDirection.Y, inRoutingHeight);

            var points = new List<XYZ> { j1, p2 };
            points.AddRange(inCrossroads);

            var distances = new double[points.Count];
            var previous = new int[points.Count];
            var visited = new bool[points.Count];

            for (var i = 0; i < points.Count; i++)
            {
                distances[i] = double.MaxValue;
                previous[i] = -1;
            }

            distances[0] = 0; // j1

            for (var iteration = 0; iteration < points.Count; iteration++)
            {
                var current = -1;
                var currentBest = double.MaxValue;

                for (var i = 0; i < points.Count; i++)
                {
                    if (!visited[i] && distances[i] < currentBest)
                    {
                        currentBest = distances[i];
                        current = i;
                    }
                }

                if (current == -1)
                {
                    break;
                }

                visited[current] = true;

                for (var next = 0; next < points.Count; next++)
                {
                    if (visited[next])
                    {
                        continue;
                    }

                    var sameX = Math.Abs(points[current].X - points[next].X) < axisTolerance;
                    var sameY = Math.Abs(points[current].Y - points[next].Y) < axisTolerance;

                    if (!sameX && !sameY)
                    {
                        continue;
                    }

                    var newDistance = distances[current] + points[current].DistanceTo(points[next]);

                    if (newDistance < distances[next])
                    {
                        distances[next] = newDistance;
                        previous[next] = current;
                    }
                }
            }

            const int p2Index = 1;

            if (distances[p2Index] >= double.MaxValue)
            {
                return null;
            }

            var pathIndices = new List<int>();
            var cursor = p2Index;

            while (cursor != -1)
            {
                pathIndices.Add(cursor);
                cursor = previous[cursor];
            }

            pathIndices.Reverse();

            var waypoints = new List<XYZ> { inFrom };
            waypoints.AddRange(pathIndices.Select(index => points[index]));
            waypoints.Add(inTo);

            return waypoints;
        }

        // True if any segment of inWaypoints properly crosses any segment in
        // inExistingSegments, in plan (X,Y only - different routing heights
        // don't conflict physically, but "crossing" here means overlapping
        // in plan view, which is what's visually confusing on the overlay).
        // Segments that only touch - e.g. two paths sharing the same
        // source's location - don't count; only a genuine mid-segment
        // crossing does.
        public static bool CrossesAny(IReadOnlyList<XYZ> inWaypoints, IReadOnlyList<(XYZ A, XYZ B)> inExistingSegments)
        {
            for (var i = 0; i < inWaypoints.Count - 1; i++)
            {
                for (var j = 0; j < inExistingSegments.Count; j++)
                {
                    if (SegmentsCross(inWaypoints[i], inWaypoints[i + 1], inExistingSegments[j].A, inExistingSegments[j].B))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool SegmentsCross(XYZ p1, XYZ p2, XYZ p3, XYZ p4)
        {
            double Orientation(XYZ o, XYZ a, XYZ b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

            var d1 = Orientation(p3, p4, p1);
            var d2 = Orientation(p3, p4, p2);
            var d3 = Orientation(p1, p2, p3);
            var d4 = Orientation(p1, p2, p4);

            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        // Draws just a circle at every node, no edges - green for sources,
        // blue for receptacles.
        public void DrawNodes()
        {
            foreach (var node in _graphNodeMap.Values)
            {
                DebugDraw.Circle(null, node.Location, 1.0, node.IsSource ? Colors.Green : Colors.Blue);
            }
        }

        // Draws exclusively from Solution - the fully solved graph
        // GraphTestFunctions.MergeParallelRuns builds - rather than reaching
        // into this instance's own node/neighbor/path state directly, so
        // there's one authoritative source for what gets rendered. Clears
        // the overlay first: every draw call below creates a brand new
        // element (id: null) rather than replacing one, so without this,
        // repeated builds would just keep accumulating old drawings on top
        // of new ones instead of replacing them.
        public void DrawGraph()
        {
            DebugDraw.ClearAll();

            if (Solution == null)
            {
                return;
            }

            foreach (var node in Solution.SourceNodes.Values)
            {
                DebugDraw.Circle(null, node.Location, 1.0, Colors.Green);
            }

            foreach (var node in Solution.NonSourceNodes.Values)
            {
                DebugDraw.Circle(null, node.Location, 1.0, Colors.Blue);
            }

            foreach (var path in Solution.NonSourcePaths.Values)
            {
                for (var i = 0; i < path.Waypoints.Count - 1; i++)
                {
                    if (path.MergedSegments[i] > 1)
                    {
                        DebugDraw.ConduitRun(path.Waypoints[i], path.Waypoints[i + 1], color: Colors.Purple);
                    }
                    else
                    {
                        DebugDraw.Line(null, path.Waypoints[i], path.Waypoints[i + 1]);
                    }
                }
            }
        }
    }
}
