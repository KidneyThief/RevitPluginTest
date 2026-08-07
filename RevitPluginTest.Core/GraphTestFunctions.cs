using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitPluginTest;
using Colors = System.Windows.Media.Colors;

namespace RevitPluginTest.Core
{
    public static class GraphTestFunctions
    {
        // Core-owned instance, referenced only by Core's own code (this class)
        // - safe across reload for the same reason DebugOverlay._window is:
        // when the ALC unloads, this field and everything it points to are
        // collected together. Its contents don't survive a reload though,
        // same as DebugDraw's primitives - ReloadCore() tears down the whole
        // assembly this lives in.
        private static readonly ConduitGraphTest _graph = new();
        private static bool _allowIntersections = true;
        private static double _runTolerance = 2.0;

        public static bool AllowIntersections => _allowIntersections;
        public static double RunTolerance => _runTolerance;

        // Called by the panel's Allow Intersections checkbox. Only
        // BuildGraph reads this - when unchecked, it prefers a
        // (possibly longer) route to a different source over one that
        // crosses a path already placed for an earlier receptacle.
        [Schedulable("SetAllowIntersections", Quiet = true)]
        public static bool SetAllowIntersections(bool allow)
        {
            _allowIntersections = allow;
            return true;
        }

        // Called by the panel's Run Tolerance slider. Only BuildGraph
        // reads this - the maximum perpendicular distance between two
        // parallel path segments (from different receptacles) for
        // MergeParallelRuns to treat them as the same conduit run. 0
        // disables merging entirely.
        [Schedulable("SetRunTolerance", Quiet = true)]
        public static bool SetRunTolerance(double tolerance)
        {
            _runTolerance = tolerance;
            return true;
        }

        [Schedulable("DrawGraph")]
        public static bool DrawGraph()
        {
            _graph.DrawGraph();
            return true;
        }

        [Schedulable("DrawNodes")]
        public static bool DrawNodes()
        {
            DebugOverlay.ClearOverlay();
            _graph.DrawNodes();
            return true;
        }

        // First parameter is UIApplication, so Scheduler.Invoke supplies it
        // automatically. Adds a node for each currently selected element,
        // located at its bounding box center (same representative-point
        // approach DrawSelected uses, since it works regardless of element
        // type). The element's own ElementId becomes the node's ID.
        [Schedulable("AddReceptaclesToGraph")]
        public static bool AddReceptaclesToGraph(UIApplication uiApp)
            => AddSelectedToGraph(uiApp, isSource: false, "AddReceptaclesToGraph");

        [Schedulable("AddSourcesToGraph")]
        public static bool AddSourcesToGraph(UIApplication uiApp)
            => AddSelectedToGraph(uiApp, isSource: true, "AddSourcesToGraph");

        private static bool AddSelectedToGraph(UIApplication uiApp, bool isSource, string logPrefix)
        {
            var uidoc = uiApp.ActiveUIDocument;

            if (uidoc == null)
            {
                Logger.Log($"{logPrefix}: no active document.");
                return false;
            }

            var selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
            {
                Logger.Log($"{logPrefix}: nothing selected.");
                return false;
            }

            _graph.RemoveNodesByType(isSource);

            var added = 0;

            foreach (var id in selectedIds)
            {
                var bbox = uidoc.Document.GetElement(id)?.get_BoundingBox(null);

                if (bbox == null)
                {
                    continue;
                }

                var center = (bbox.Min + bbox.Max).Multiply(0.5);
                var node = new tNode((int)id.Value, center, isSource);

                if (_graph.AddNode(in node))
                {
                    added++;
                    DebugDraw.Circle(null, center, 1.0, Colors.Blue, duration: 3);
                }
            }

            Logger.Log($"{logPrefix}: added {added} node(s).");
            return true;
        }

        // First parameter is UIApplication, so Scheduler.Invoke supplies it
        // automatically (computing conduit paths requires looking up each
        // node's host). Draws the result automatically on success, or
        // clears the overlay if the build couldn't run at all (e.g. no
        // active document) - either way the overlay ends up reflecting the
        // current graph state rather than a stale one from before the click.
        // Each non-source node gets exactly one edge, to whichever source
        // gives it the shortest conduit path. When _allowIntersections is
        // true, every node's edge is independent of every other node's (a
        // receptacle's path never shares geometry with another
        // receptacle's), so minimizing each node's own path length also
        // minimizes the longest of them - no tradeoff to make between
        // nodes, unlike a shared-tree strategy. When it's false, nodes are
        // no longer independent: each one prefers the shortest source that
        // doesn't cross a path already placed for an earlier node in this
        // same build, falling back to the unconstrained shortest if none
        // avoid crossing. This is greedy/order-dependent (not a global
        // minimum-crossings solution), but keeps the change simple.
        [Schedulable("BuildGraph")]
        public static bool BuildGraph(UIApplication uiApp)
        {
            _graph.ClearNeighbors();

            var uidoc = uiApp.ActiveUIDocument;

            if (uidoc == null)
            {
                Logger.Log("BuildGraph: no active document.");
                DebugOverlay.ClearOverlay();
                return false;
            }

            var connected = 0;
            var skipped = 0;
            var forcedCrossings = 0;
            var placedSegments = new List<(XYZ A, XYZ B)>();

            foreach (var node in _graph.Nodes)
            {
                if (node.IsSource)
                {
                    continue;
                }

                var conduitPaths = ComputeConduitPathsToSources(uidoc.Document, in node, "BuildGraph");

                if (conduitPaths.Count == 0)
                {
                    skipped++;
                    continue;
                }

                var chosenConduitPath = conduitPaths.OrderBy(conduitPath => conduitPath.Length).First();

                if (!_allowIntersections)
                {
                    var nonCrossing = conduitPaths
                        .Where(conduitPath => !ConduitGraphTest.CrossesAny(conduitPath.Waypoints, placedSegments))
                        .OrderBy(conduitPath => conduitPath.Length)
                        .ToList();

                    if (nonCrossing.Count > 0)
                    {
                        chosenConduitPath = nonCrossing[0];
                    }
                    else
                    {
                        forcedCrossings++;
                    }
                }

                _graph.AddNeighbor(chosenConduitPath.SourceId, node.ID);
                _graph.SetConduitPath(node.ID, chosenConduitPath);

                for (var i = 0; i < chosenConduitPath.Waypoints.Count - 1; i++)
                {
                    placedSegments.Add((chosenConduitPath.Waypoints[i], chosenConduitPath.Waypoints[i + 1]));
                }

                connected++;
            }

            var forcedSuffix = forcedCrossings > 0 ? $", {forcedCrossings} could not avoid crossing another path" : "";
            var skippedSuffix = skipped > 0 ? $", {skipped} skipped (no usable host)" : "";
            Logger.Log($"BuildGraph: connected {connected} node(s) to a source via conduit path{forcedSuffix}{skippedSuffix}.");

            MergeParallelRuns(uidoc.Document);
            _graph.DrawGraph();
            return true;
        }

        // A horizontal, axis-aligned portion of one node's conduit path -
        // AlongX true means it runs along X (Offset is its constant Y);
        // false means it runs along Y (Offset is its constant X). Forward
        // records whether the path's own travel direction goes from
        // SpanMin to SpanMax (true) or the reverse, so a merge can rebuild
        // the replacement points in the original direction.
        private readonly struct RunSegment
        {
            public int NodeId { get; }
            public int Index { get; }
            public bool AlongX { get; }
            public double Offset { get; }
            public double SpanMin { get; }
            public double SpanMax { get; }
            public double Z { get; }
            public bool Forward { get; }

            public RunSegment(int nodeId, int index, bool alongX, double offset, double spanMin, double spanMax, double z, bool forward)
            {
                NodeId = nodeId;
                Index = index;
                AlongX = alongX;
                Offset = offset;
                SpanMin = spanMin;
                SpanMax = spanMax;
                Z = z;
                Forward = forward;
            }
        }

        // A conduit run under construction: its line (AlongX/Offset/Z) and
        // span are fixed the moment it's created (from its first two
        // members) and never move afterward - later joiners must fit this
        // exact line, rather than nudging it via a running average. That's
        // what keeps every member's Count in sync: since a run is the one
        // and only place membership is recorded, Members.Count is always
        // the true, final total - not a per-pair sum that can drift out of
        // sync with paths that joined the same run earlier or later.
        private sealed class RunBuilder
        {
            public bool AlongX;
            public double Offset;
            public double Z;
            public double SpanMin;
            public double SpanMax;
            public readonly List<RunSegment> Members = new();
        }

        // Vertical stubs (constant X and Y) and diagonals (shouldn't happen,
        // given the right-angle-only routing this all relies on) aren't
        // mergeable runs - only null is returned for those.
        private static RunSegment? ClassifySegment(int nodeId, int index, XYZ start, XYZ end)
        {
            const double epsilon = 1e-6;

            var sameX = Math.Abs(start.X - end.X) < epsilon;
            var sameY = Math.Abs(start.Y - end.Y) < epsilon;
            var sameZ = Math.Abs(start.Z - end.Z) < epsilon;

            if (!sameZ || (sameX && sameY))
            {
                return null;
            }

            if (sameY)
            {
                var forward = start.X <= end.X;
                return new RunSegment(nodeId, index, true, start.Y, Math.Min(start.X, end.X), Math.Max(start.X, end.X), start.Z, forward);
            }

            if (sameX)
            {
                var forward = start.Y <= end.Y;
                return new RunSegment(nodeId, index, false, start.X, Math.Min(start.Y, end.Y), Math.Max(start.Y, end.Y), start.Z, forward);
            }

            return null;
        }

        private static bool CanMerge(RunSegment a, RunSegment b, double tolerance)
        {
            // Z (routing height) came from each receptacle's OWN host's
            // bounding box top, so two different receptacles will almost
            // never match exactly - was previously gated by a fixed 1e-6
            // epsilon here, independent of the tolerance slider, which
            // silently blocked every merge no matter how high the slider
            // was set. Uses the same tolerance for both now.
            return a.NodeId != b.NodeId
                && a.AlongX == b.AlongX
                && Math.Abs(a.Z - b.Z) <= tolerance
                && Math.Abs(a.Offset - b.Offset) <= tolerance
                && a.SpanMin <= b.SpanMax && b.SpanMin <= a.SpanMax;
        }

        private static bool FitsRun(RunSegment segment, RunBuilder run, double tolerance)
        {
            return segment.AlongX == run.AlongX
                && Math.Abs(segment.Z - run.Z) <= tolerance
                && Math.Abs(segment.Offset - run.Offset) <= tolerance
                && segment.SpanMin <= run.SpanMax && run.SpanMin <= segment.SpanMax;
        }

        // Post-process over the paths BuildGraph just built: clusters
        // parallel, close (within RunTolerance), overlapping segments from
        // DIFFERENT receptacles' paths into conduit runs - representing
        // conduits bundled into one physical run - then builds the fully
        // solved graph (nodes, whatever routing corners aren't already
        // covered by a run, and the runs themselves) and stores it as
        // _graph.Solution. Every new point a run introduces (its own two
        // ends, and each member's jog onto/off of it) is verified as
        // interior before being committed; a run that fails is discarded
        // entirely rather than risk a route through the exterior.
        private static void MergeParallelRuns(Document document)
        {
            var waypointsByNode = _graph.ConduitPaths.ToDictionary(kv => kv.Key, kv => new List<XYZ>(kv.Value.Waypoints));

            var segments = new List<RunSegment>();

            foreach (var (nodeId, waypoints) in waypointsByNode)
            {
                for (var i = 0; i < waypoints.Count - 1; i++)
                {
                    var classified = ClassifySegment(nodeId, i, waypoints[i], waypoints[i + 1]);

                    if (classified != null)
                    {
                        segments.Add(classified.Value);
                    }
                }
            }

            var runs = new List<RunBuilder>();

            if (_runTolerance > 0)
            {
                var assigned = new HashSet<(int NodeId, int Index)>();

                foreach (var segment in segments)
                {
                    var key = (segment.NodeId, segment.Index);

                    if (assigned.Contains(key))
                    {
                        continue;
                    }

                    var matchingRun = runs.FirstOrDefault(run => FitsRun(segment, run, _runTolerance));

                    if (matchingRun != null)
                    {
                        matchingRun.Members.Add(segment);
                        assigned.Add(key);
                        continue;
                    }

                    RunSegment? partner = null;

                    foreach (var candidate in segments)
                    {
                        var candidateKey = (candidate.NodeId, candidate.Index);

                        if (candidateKey == key || assigned.Contains(candidateKey))
                        {
                            continue;
                        }

                        if (CanMerge(segment, candidate, _runTolerance))
                        {
                            partner = candidate;
                            break;
                        }
                    }

                    if (partner == null)
                    {
                        continue;
                    }

                    var newRun = new RunBuilder
                    {
                        AlongX = segment.AlongX,
                        Offset = (segment.Offset + partner.Value.Offset) / 2.0,
                        Z = (segment.Z + partner.Value.Z) / 2.0,
                        SpanMin = Math.Max(segment.SpanMin, partner.Value.SpanMin),
                        SpanMax = Math.Min(segment.SpanMax, partner.Value.SpanMax)
                    };

                    newRun.Members.Add(segment);
                    newRun.Members.Add(partner.Value);
                    runs.Add(newRun);
                    assigned.Add(key);
                    assigned.Add((partner.Value.NodeId, partner.Value.Index));
                }
            }

            // Room checks happen at each member's own (low) height, not the
            // run's Z (a routing height - the top of a wall) - a room's
            // recognized vertical extent doesn't reach that high, so
            // testing up there reads every point as "outside" regardless of
            // its XY position. The points actually stored/drawn still use
            // the run's Z, the shared routing plane, since that's what the
            // rest of each path is drawn at.
            double TestZFor(int nodeId, double fallback) => _graph.TryGetNode(nodeId, out var node) ? node.Location.Z : fallback;
            XYZ MakeRunPoint(RunBuilder run, double along, double offset) => run.AlongX ? new XYZ(along, offset, run.Z) : new XYZ(offset, along, run.Z);

            var validRuns = new List<RunBuilder>();
            var rejectedRoomCheck = 0;

            foreach (var run in runs)
            {
                var trunkTestZ = run.Members.Average(member => TestZFor(member.NodeId, member.Z));
                var trunkStartTest = run.AlongX ? new XYZ(run.SpanMin, run.Offset, trunkTestZ) : new XYZ(run.Offset, run.SpanMin, trunkTestZ);
                var trunkEndTest = run.AlongX ? new XYZ(run.SpanMax, run.Offset, trunkTestZ) : new XYZ(run.Offset, run.SpanMax, trunkTestZ);

                if (document.GetRoomAtPoint(trunkStartTest) == null || document.GetRoomAtPoint(trunkEndTest) == null)
                {
                    rejectedRoomCheck++;
                    continue;
                }

                var allMembersValid = true;

                foreach (var member in run.Members)
                {
                    var testZ = TestZFor(member.NodeId, member.Z);
                    var (firstBound, secondBound) = member.Forward ? (run.SpanMin, run.SpanMax) : (run.SpanMax, run.SpanMin);

                    var p1Test = run.AlongX ? new XYZ(firstBound, member.Offset, testZ) : new XYZ(member.Offset, firstBound, testZ);
                    var p4Test = run.AlongX ? new XYZ(secondBound, member.Offset, testZ) : new XYZ(member.Offset, secondBound, testZ);

                    if (document.GetRoomAtPoint(p1Test) == null || document.GetRoomAtPoint(p4Test) == null)
                    {
                        allMembersValid = false;
                        break;
                    }
                }

                if (!allMembersValid)
                {
                    rejectedRoomCheck++;
                    continue;
                }

                validRuns.Add(run);
            }

            Logger.Log($"BuildGraph: merge scan - {segments.Count} horizontal segment(s), {runs.Count} candidate run(s), {rejectedRoomCheck} rejected by room check, {validRuns.Count} conduit run(s) formed.");

            // nodeId -> (originalIndex, replacementPoints, thisRun'sCount),
            // applied back-to-front per node (across ALL runs touching that
            // node, not just one) so earlier splices don't shift indices a
            // later one still needs.
            var replacementsByNode = new Dictionary<int, List<(int Index, List<XYZ> Points, int Count)>>();
            var conduitRunRecords = new List<tConduitRun>();

            foreach (var run in validRuns)
            {
                foreach (var member in run.Members)
                {
                    var (firstBound, secondBound) = member.Forward ? (run.SpanMin, run.SpanMax) : (run.SpanMax, run.SpanMin);

                    var p0 = MakeRunPoint(run, member.Forward ? member.SpanMin : member.SpanMax, member.Offset);
                    var p1 = MakeRunPoint(run, firstBound, member.Offset);
                    var p2 = MakeRunPoint(run, firstBound, run.Offset);
                    var p3 = MakeRunPoint(run, secondBound, run.Offset);
                    var p4 = MakeRunPoint(run, secondBound, member.Offset);
                    var p5 = MakeRunPoint(run, member.Forward ? member.SpanMax : member.SpanMin, member.Offset);

                    if (!replacementsByNode.TryGetValue(member.NodeId, out var list))
                    {
                        list = new List<(int, List<XYZ>, int)>();
                        replacementsByNode[member.NodeId] = list;
                    }

                    list.Add((member.Index, new List<XYZ> { p0, p1, p2, p3, p4, p5 }, run.Members.Count));
                }

                var runStart = MakeRunPoint(run, run.SpanMin, run.Offset);
                var runEnd = MakeRunPoint(run, run.SpanMax, run.Offset);
                conduitRunRecords.Add(new tConduitRun(runStart, runEnd, run.Members.Select(member => member.NodeId).ToList()));
            }

            foreach (var (nodeId, replacements) in replacementsByNode)
            {
                var waypoints = waypointsByNode[nodeId];
                var counts = new List<int>(Enumerable.Repeat(1, waypoints.Count - 1));

                foreach (var (index, points, count) in replacements.OrderByDescending(r => r.Index))
                {
                    waypoints.RemoveRange(index, 2);
                    waypoints.InsertRange(index, points);

                    // points is always [p0, p1, p2, p3, p4, p5] - 5 segments
                    // replacing the 1 they came from; only p2->p3 (index 2)
                    // is the actual shared trunk portion.
                    counts.RemoveRange(index, 1);
                    counts.InsertRange(index, new[] { 1, 1, count, 1, 1 });
                }

                if (!_graph.ConduitPaths.TryGetValue(nodeId, out var originalPath))
                {
                    continue;
                }

                var length = 0.0;

                for (var i = 0; i < waypoints.Count - 1; i++)
                {
                    length += waypoints[i].DistanceTo(waypoints[i + 1]);
                }

                _graph.SetConduitPath(nodeId, new tConduitPath(originalPath.SourceId, length, waypoints, counts));
            }

            // Nodes untouched by any merge still need their (unmerged, all-1)
            // tConduitPath re-set here, since waypointsByNode's copies -
            // not _graph.ConduitPaths' own entries - are the up-to-date ones.
            foreach (var (nodeId, waypoints) in waypointsByNode)
            {
                if (replacementsByNode.ContainsKey(nodeId) || !_graph.ConduitPaths.TryGetValue(nodeId, out var originalPath))
                {
                    continue;
                }

                _graph.SetConduitPath(nodeId, new tConduitPath(originalPath.SourceId, originalPath.Length, waypoints));
            }

            var sourceNodes = _graph.Nodes.Where(node => node.IsSource).ToDictionary(node => node.ID);
            var nonSourceNodes = _graph.Nodes.Where(node => !node.IsSource).ToDictionary(node => node.ID);

            var intermediatePoints = new List<XYZ>();
            var seenIntermediatePoints = new HashSet<(long, long, long)>();

            (long, long, long) RoundKey(XYZ point) => ((long)Math.Round(point.X * 1000), (long)Math.Round(point.Y * 1000), (long)Math.Round(point.Z * 1000));

            bool IsConduitRunEndpoint(XYZ point)
            {
                const double epsilon = 1e-6;

                foreach (var run in conduitRunRecords)
                {
                    if (point.DistanceTo(run.Start) < epsilon || point.DistanceTo(run.End) < epsilon)
                    {
                        return true;
                    }
                }

                return false;
            }

            foreach (var waypoints in waypointsByNode.Values)
            {
                for (var i = 1; i < waypoints.Count - 1; i++)
                {
                    var point = waypoints[i];

                    if (IsConduitRunEndpoint(point))
                    {
                        continue;
                    }

                    if (seenIntermediatePoints.Add(RoundKey(point)))
                    {
                        intermediatePoints.Add(point);
                    }
                }
            }

            _graph.SetSolution(new tGraphSolution(sourceNodes, nonSourceNodes, intermediatePoints, conduitRunRecords, _graph.ConduitPaths));
        }

        // First parameter is UIApplication, so Scheduler.Invoke supplies it
        // automatically. Takes the first currently selected element, which
        // must already be a non-source node in the graph (add it via Add
        // Receptacles first).
        [Schedulable("FindNearestSource")]
        public static bool FindNearestSource(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;

            if (uidoc == null)
            {
                Logger.Log("FindNearestSource: no active document.");
                return false;
            }

            var selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
            {
                Logger.Log("FindNearestSource: nothing selected.");
                return false;
            }

            var elementId = selectedIds.First();

            if (!_graph.TryGetNode((int)elementId.Value, out var node))
            {
                Logger.Log("FindNearestSource: selected element isn't a node in the graph - add it via Add Receptacles first.");
                return false;
            }

            if (node.IsSource)
            {
                Logger.Log("FindNearestSource: selected node is a source, not a receptacle.");
                return false;
            }

            var conduitPaths = ComputeConduitPathsToSources(uidoc.Document, in node, "FindNearestSource");

            if (conduitPaths.Count == 0)
            {
                Logger.Log("FindNearestSource: could not compute a conduit path to any source - the selected element and at least one source both need a usable host.");
                return false;
            }

            var shortestConduitPath = conduitPaths.OrderBy(conduitPath => conduitPath.Length).First();

            foreach (var conduitPath in conduitPaths)
            {
                var color = conduitPath.SourceId == shortestConduitPath.SourceId ? Colors.Green : Colors.Red;

                for (var i = 0; i < conduitPath.Waypoints.Count - 1; i++)
                {
                    DebugDraw.Line(null, conduitPath.Waypoints[i], conduitPath.Waypoints[i + 1], color, duration: 5);
                }
            }

            Logger.Log($"FindNearestSource: nearest source is node {shortestConduitPath.SourceId}, conduit path length {shortestConduitPath.Length:F2}.");
            return true;
        }

        // Computes a candidate conduit path from inFromNode to every source
        // node in the graph - the right-angle, wall-perpendicular route
        // described on ConduitGraphTest.RouteBetween, not a straight line.
        // Shared by FindNearestSource (one selected node) and
        // BuildGraph (every non-source node in turn). inLogPrefix
        // matches whichever of those is calling, so ambiguous-host warnings
        // read consistently with the rest of that command's log output.
        private static List<tConduitPath> ComputeConduitPathsToSources(Document document, in tNode inFromNode, string inLogPrefix)
        {
            var host = (document.GetElement(new ElementId((long)inFromNode.ID)) as FamilyInstance)?.Host;
            var hostBBox = host?.get_BoundingBox(null);

            if (host == null || hostBBox == null)
            {
                return new List<tConduitPath>();
            }

            var fromDirections = GetHostNormal(document, host, inFromNode.Location);

            if (fromDirections.Count == 0)
            {
                return new List<tConduitPath>();
            }

            if (fromDirections.Count > 1)
            {
                Logger.Log($"{inLogPrefix}: no room found on either side of the receptacle's host - trying both directions and using whichever gives the shorter conduit path.");
            }

            var routingHeight = hostBBox.Max.Z;
            var routedFrom = new XYZ(inFromNode.Location.X, inFromNode.Location.Y, routingHeight);
            var crossroads = BuildCrossroadPool(document, routingHeight);

            var conduitPaths = new List<tConduitPath>();

            foreach (var candidate in _graph.Nodes)
            {
                if (!candidate.IsSource)
                {
                    continue;
                }

                var candidateHost = (document.GetElement(new ElementId((long)candidate.ID)) as FamilyInstance)?.Host;

                if (candidateHost == null)
                {
                    continue;
                }

                var toDirections = GetHostNormal(document, candidateHost, candidate.Location);

                if (toDirections.Count == 0)
                {
                    continue;
                }

                if (toDirections.Count > 1)
                {
                    Logger.Log($"{inLogPrefix}: no room found on either side of source node {candidate.ID}'s host - trying both directions and using whichever gives the shorter conduit path.");
                }

                var routedTo = new XYZ(candidate.Location.X, candidate.Location.Y, routingHeight);
                tConduitPath? bestForCandidate = null;

                foreach (var fromDirection in fromDirections)
                {
                    foreach (var toDirection in toDirections)
                    {
                        var middle = ConduitGraphTest.RouteBetween(routedFrom, fromDirection, routedTo, toDirection, routingHeight, crossroads);

                        if (middle == null)
                        {
                            continue;
                        }

                        var waypoints = new List<XYZ> { inFromNode.Location };
                        waypoints.AddRange(middle);
                        waypoints.Add(candidate.Location);

                        var length = 0.0;

                        for (var i = 0; i < waypoints.Count - 1; i++)
                        {
                            length += waypoints[i].DistanceTo(waypoints[i + 1]);
                        }

                        if (bestForCandidate == null || length < bestForCandidate.Value.Length)
                        {
                            bestForCandidate = new tConduitPath(candidate.ID, length, waypoints);
                        }
                    }
                }

                if (bestForCandidate != null)
                {
                    conduitPaths.Add(bestForCandidate.Value);
                }
                else
                {
                    Logger.Log($"{inLogPrefix}: no interior-only conduit route found to source node {candidate.ID} - skipping it.");
                }
            }

            return conduitPaths;
        }

        // Candidate waypoints for routing the middle of a conduit path
        // through interior space instead of a raw geometric elbow that
        // might cut through a wall or the exterior. For every node with a
        // resolved interior direction, and every OTHER node: whichever axis
        // that direction points along (world X or Y - wall directions are
        // assumed cardinal, same as the crossroad-lookup rule itself)
        // decides which coordinate to take from the other node and which to
        // keep from this node. The resulting point is kept only if it's
        // actually inside a room. Computed once per receptacle (reused
        // across every candidate source), since it only depends on the
        // graph's current nodes, not on which pair is being routed.
        private static List<XYZ> BuildCrossroadPool(Document document, double routingHeight)
        {
            var nodeDirections = new List<(tNode Node, List<XYZ> Directions)>();

            foreach (var node in _graph.Nodes)
            {
                var host = (document.GetElement(new ElementId((long)node.ID)) as FamilyInstance)?.Host;

                if (host == null)
                {
                    continue;
                }

                var directions = GetHostNormal(document, host, node.Location);

                if (directions.Count > 0)
                {
                    nodeDirections.Add((node, directions));
                }
            }

            var crossroads = new List<XYZ>();

            foreach (var (node, directions) in nodeDirections)
            {
                foreach (var direction in directions)
                {
                    var useOtherNodesX = Math.Abs(direction.X) >= Math.Abs(direction.Y);

                    foreach (var (otherNode, _) in nodeDirections)
                    {
                        if (otherNode.ID == node.ID)
                        {
                            continue;
                        }

                        // Tested at the node's own height, not routingHeight
                        // (the top of a wall) - a room's recognized vertical
                        // extent is its own Limit Offset, typically well
                        // below the top of a full-height wall, so testing up
                        // there reads every point as "outside" regardless of
                        // its XY position.
                        var testPoint = useOtherNodesX
                            ? new XYZ(otherNode.Location.X, node.Location.Y, node.Location.Z)
                            : new XYZ(node.Location.X, otherNode.Location.Y, node.Location.Z);

                        if (document.GetRoomAtPoint(testPoint) != null)
                        {
                            crossroads.Add(new XYZ(testPoint.X, testPoint.Y, routingHeight));
                        }
                    }
                }
            }

            return crossroads;
        }

        // Wall hosts give a perpendicular-to-top-edge candidate via
        // Wall.Orientation. Any other host type falls back to the direction
        // from its bounding box center to the node - an approximation, but
        // keeps this working for non-wall hosts. Either way this is just ONE
        // of the two possible perpendicular directions; ResolveInwardDirectionViaRooms
        // picks whichever actually points into a Room. If that's ambiguous
        // (no room on either side, or a room on both), both directions are
        // returned and the caller tries both, keeping whichever gives the
        // shorter conduit path.
        private static List<XYZ> GetHostNormal(Document document, Element host, XYZ nodeLocation)
        {
            XYZ candidateDirection;
            double testOffset;

            if (host is Wall wall)
            {
                var orientation = wall.Orientation;
                candidateDirection = new XYZ(orientation.X, orientation.Y, 0).Normalize();

                // nodeLocation could sit anywhere across the wall's
                // thickness (near face, far face, centerline), so the probe
                // has to clear the whole width to guarantee it reaches past
                // the far face, plus a margin to land clearly inside the room.
                testOffset = wall.Width + 0.5;
            }
            else
            {
                var bbox = host.get_BoundingBox(null);

                if (bbox == null)
                {
                    return new List<XYZ>();
                }

                var center = (bbox.Min + bbox.Max).Multiply(0.5);
                var direction = new XYZ(nodeLocation.X - center.X, nodeLocation.Y - center.Y, 0);

                if (direction.IsZeroLength())
                {
                    return new List<XYZ>();
                }

                candidateDirection = direction.Normalize();
                testOffset = 1.0;
            }

            var roomBasedDirection = ResolveInwardDirectionViaRooms(document, nodeLocation, candidateDirection, testOffset);

            if (roomBasedDirection != null)
            {
                return new List<XYZ> { roomBasedDirection };
            }

            return new List<XYZ> { candidateDirection, candidateDirection.Negate() };
        }

        // Offsets a small distance off the wall face on each side and asks
        // Revit which side (if either) lands inside an actual Room - the
        // reliable way to tell "inside" from "outside". Null if neither/both
        // sides resolve to a room (e.g. an interior partition with a room on
        // both sides, or no rooms placed in the model at all) - caller tries
        // both directions in that case. inTestOffset must clear the host's
        // full thickness - a probe that only pokes partway into a thick wall
        // never reaches real room space and reads as "no room" on a side
        // that's actually interior.
        private static XYZ? ResolveInwardDirectionViaRooms(Document document, XYZ nodeLocation, XYZ candidateDirection, double inTestOffset)
        {
            var positiveSide = nodeLocation + candidateDirection.Multiply(inTestOffset);
            var negativeSide = nodeLocation - candidateDirection.Multiply(inTestOffset);

            var positiveRoom = document.GetRoomAtPoint(positiveSide);
            var negativeRoom = document.GetRoomAtPoint(negativeSide);

            if (positiveRoom != null && negativeRoom == null)
            {
                return candidateDirection;
            }

            if (negativeRoom != null && positiveRoom == null)
            {
                return candidateDirection.Negate();
            }

            return null;
        }

        // Called by CoreLifecycle.Initialize() before Unload() - not
        // individually schedulable, since Initialize() is the one reload-time
        // entry point Host knows about. Not strictly required for pure
        // in-memory data (the ALC unload would collect it regardless), but
        // makes the cleanup visible and gives future graph state - anything
        // that does need explicit teardown - a hook to plug into.
        public static void ResetGraph()
        {
            _graph.ClearGraph();
        }

        // First parameter is UIApplication, so Scheduler.Invoke supplies it
        // automatically - type SelectAllSimilar() in the console or click
        // "Select All", no need to pass one yourself. Takes the type of
        // whatever's currently selected (the first element, if several are
        // selected) and replaces the selection with every instance of that
        // same type in the project.
        [Schedulable("SelectAllSimilar")]
        public static bool SelectAllSimilar(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;

            if (uidoc == null)
            {
                Logger.Log("SelectAllSimilar: no active document.");
                return false;
            }

            var selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
            {
                Logger.Log("SelectAllSimilar: nothing selected.");
                return false;
            }

            var document = uidoc.Document;
            var reference = document.GetElement(selectedIds.First());
            var typeId = reference?.GetTypeId();

            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                Logger.Log("SelectAllSimilar: selected element has no type.");
                return false;
            }

            var matches = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .Where(e => e.GetTypeId() == typeId)
                .Select(e => e.Id)
                .ToList();

            uidoc.Selection.SetElementIds(matches);
            Logger.Log($"SelectAllSimilar: selected {matches.Count} instance(s).");

            return true;
        }
    }
}
