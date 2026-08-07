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
        private static eGraphType _selectedGraphType = eGraphType.None;

        // Called by the panel's Graph Type dropdown (options come from
        // Enum.GetNames, so this always matches an eGraphType member name).
        [Schedulable("SetGraphType", Quiet = true)]
        public static bool SetGraphType(string graphType)
        {
            if (!Enum.TryParse<eGraphType>(graphType, out var parsed))
            {
                Logger.Log($"SetGraphType: unknown graph type '{graphType}'.");
                return false;
            }

            _selectedGraphType = parsed;
            return true;
        }

        [Schedulable("DrawGraph")]
        public static bool DrawGraph()
        {
            _graph.DrawGraph();
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

        // Dispatches to one of three strategies based on the panel's Graph
        // Type dropdown. First parameter is UIApplication, so Scheduler.Invoke
        // supplies it automatically - only BuildConduitGraph actually needs
        // it (computing conduit paths requires looking up each node's host).
        [Schedulable("BuildGraph")]
        public static bool BuildGraph(UIApplication uiApp)
        {
            _graph.ClearNeighbors();

            switch (_selectedGraphType)
            {
                case eGraphType.None:
                    BuildNoneGraph();
                    break;
                case eGraphType.Circuits:
                    BuildCircuitsGraph();
                    break;
                case eGraphType.Conduit:
                    BuildConduitGraph(uiApp);
                    break;
            }

            return true;
        }

        private static void BuildNoneGraph()
        {
            Logger.Log("BuildGraph: graph type is None - nothing to build.");
        }

        private static void BuildCircuitsGraph()
        {
            _graph.BuildCircuits();
        }

        // Each non-source node gets exactly one edge, straight to whichever
        // source gives it the shortest conduit path. Since every node's edge
        // is independent of every other node's (a receptacle's path never
        // shares geometry with another receptacle's), minimizing each node's
        // own path length also minimizes the longest of them - there's no
        // tradeoff to make between nodes, unlike a shared-tree strategy.
        private static void BuildConduitGraph(UIApplication uiApp)
        {
            var uidoc = uiApp.ActiveUIDocument;

            if (uidoc == null)
            {
                Logger.Log("BuildGraph: no active document.");
                return;
            }

            var connected = 0;
            var skipped = 0;

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

                var shortestConduitPath = conduitPaths.OrderBy(conduitPath => conduitPath.Length).First();
                _graph.AddNeighbor(shortestConduitPath.SourceId, node.ID);
                _graph.SetConduitPath(node.ID, shortestConduitPath);
                connected++;
            }

            var skippedSuffix = skipped > 0 ? $", {skipped} skipped (no usable host)" : "";
            Logger.Log($"BuildGraph: connected {connected} node(s) to a source via conduit path{skippedSuffix}.");
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
        // BuildConduitGraph (every non-source node in turn). inLogPrefix
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
