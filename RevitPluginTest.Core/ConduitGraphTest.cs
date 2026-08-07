using System.Linq;
using Autodesk.Revit.DB;
using Colors = System.Windows.Media.Colors;

namespace RevitPluginTest.Core
{
    public enum eGraphType
    {
        None,
        Circuits,
        Conduit
    }

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

        public tConduitPath(int inSourceId, double inLength, IReadOnlyList<XYZ> inWaypoints)
        {
            SourceId = inSourceId;
            Length = inLength;
            Waypoints = inWaypoints;
        }
    }

    public class ConduitGraphTest
    {
        private readonly Dictionary<int, tNode> _graphNodeMap = new();
        private readonly Dictionary<int, HashSet<int>> _neighborMap = new();

        // Right-angle route geometry for edges built by BuildConduitGraph,
        // keyed by the non-source node's id (each has exactly one). DrawGraph
        // uses this to render the actual routed path instead of a straight
        // arrow; edges from other strategies (Circuits, complete mesh) have
        // no entry here and fall back to a straight arrow.
        private readonly Dictionary<int, tConduitPath> _conduitPaths = new();

        public IEnumerable<tNode> Nodes => _graphNodeMap.Values;

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
        }

        // False if inFromId isn't a known node, or inToId was already a neighbor.
        public bool AddNeighbor(int inFromId, int inToId)
        {
            return _neighborMap.TryGetValue(inFromId, out var neighbors) && neighbors.Add(inToId);
        }

        // Records the actual routed geometry for the edge into inNonSourceId,
        // so DrawGraph can render the real right-angle path instead of a
        // straight arrow. BuildConduitGraph calls this right after AddNeighbor.
        public void SetConduitPath(int inNonSourceId, tConduitPath inPath)
        {
            _conduitPaths[inNonSourceId] = inPath;
        }

        // Multi-source Dijkstra: every source node starts already "visited"
        // with a cumulative path length of zero, then repeatedly extends
        // whichever not-yet-visited node has the shortest total path back to
        // a source - fromNode's own accumulated distance plus the direct hop
        // to it - not just the shortest single hop (that would be Prim's,
        // which minimizes total wire length but can still saddle an
        // individual node with a long path if it happens to attach deep into
        // an already-long branch). One directed edge per connection - a tree
        // needs no reverse arrow. Falls back to an arbitrary root if no node
        // is a source.
        public void BuildCircuits()
        {
            if (_graphNodeMap.Count < 2)
            {
                return;
            }

            var sourceIds = _graphNodeMap.Values.Where(node => node.IsSource).Select(node => node.ID).ToList();

            if (sourceIds.Count == 0)
            {
                sourceIds.Add(_graphNodeMap.Keys.First());
            }

            var pathLength = new Dictionary<int, double>();
            var visited = new HashSet<int>();

            foreach (var id in sourceIds)
            {
                pathLength[id] = 0;
                visited.Add(id);
            }

            while (visited.Count < _graphNodeMap.Count)
            {
                var bestPathLength = double.MaxValue;
                var bestFromId = -1;
                var bestToId = -1;

                foreach (var fromId in visited)
                {
                    var fromLocation = _graphNodeMap[fromId].Location;
                    var fromPathLength = pathLength[fromId];

                    foreach (var toId in _graphNodeMap.Keys)
                    {
                        if (visited.Contains(toId))
                        {
                            continue;
                        }

                        var candidatePathLength = fromPathLength + fromLocation.DistanceTo(_graphNodeMap[toId].Location);

                        if (candidatePathLength < bestPathLength)
                        {
                            bestPathLength = candidatePathLength;
                            bestFromId = fromId;
                            bestToId = toId;
                        }
                    }
                }

                AddNeighbor(bestFromId, bestToId);
                visited.Add(bestToId);
                pathLength[bestToId] = bestPathLength;
            }
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

        // Draws a circle at every node and an arrow to each of its neighbors -
        // or, for a conduit-routed edge, the actual right-angle path instead
        // of a straight arrow.
        public void DrawGraph()
        {
            foreach (var node in _graphNodeMap.Values)
            {
                DebugDraw.Circle(null, node.Location, 1.0, node.IsSource ? Colors.Green : Colors.Blue);

                if (!_neighborMap.TryGetValue(node.ID, out var neighbors))
                {
                    continue;
                }

                foreach (var neighborId in neighbors)
                {
                    if (!_graphNodeMap.TryGetValue(neighborId, out var neighborNode))
                    {
                        continue;
                    }

                    if (_conduitPaths.TryGetValue(neighborId, out var conduitPath) && conduitPath.SourceId == node.ID)
                    {
                        for (var i = 0; i < conduitPath.Waypoints.Count - 1; i++)
                        {
                            DebugDraw.Line(null, conduitPath.Waypoints[i], conduitPath.Waypoints[i + 1]);
                        }

                        continue;
                    }

                    DebugDraw.Arrow(null, node.Location, neighborNode.Location);
                }
            }
        }
    }
}
