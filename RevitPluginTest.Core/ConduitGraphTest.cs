using Autodesk.Revit.DB;

namespace RevitPluginTest.Core
{
    public struct tNode
    {
        public int ID;
        public XYZ Location;

        public tNode(int inId, XYZ inLocation)
        {
            ID = inId;
            Location = inLocation;
        }
    }

    public class ConduitGraphTest
    {
        private readonly Dictionary<int, tNode> _graphNodeMap = new();
        private readonly Dictionary<int, HashSet<int>> _neighborMap = new();

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

        public bool RemoveNode(int inId)
        {
            var removed = _graphNodeMap.Remove(inId);
            _neighborMap.Remove(inId);
            return removed;
        }

        public void ClearGraph()
        {
            _graphNodeMap.Clear();
            _neighborMap.Clear();
        }

        // Draws a circle at every node and an arrow to each of its neighbors.
        public void DrawGraph()
        {
            foreach (var node in _graphNodeMap.Values)
            {
                DebugDraw.Circle(null, node.Location, 1.0);

                if (!_neighborMap.TryGetValue(node.ID, out var neighbors))
                {
                    continue;
                }

                foreach (var neighborId in neighbors)
                {
                    if (_graphNodeMap.TryGetValue(neighborId, out var neighborNode))
                    {
                        DebugDraw.Arrow(null, node.Location, neighborNode.Location);
                    }
                }
            }
        }
    }
}
