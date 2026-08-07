using Autodesk.Revit.DB;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace RevitPluginTest.Core
{
    // Persistent debug primitives in model space, re-projected onto the
    // overlay every tick based on whatever view is currently active.
    // Primitives stay until explicitly cleared or their duration elapses -
    // callers don't need to re-issue them every tick to keep them visible.
    // Lives in Core, so a reload naturally resets this state along with
    // everything else.
    public static class DebugDraw
    {
        private interface IDebugElement
        {
            int Id { get; }
            double Duration { get; }
            DateTime CreatedAt { get; }
        }

        // Thickness means stroke width for Line/Circle/Arrow, font size for Text.
        public sealed record DebugLine(int Id, XYZ Start, XYZ End, Color Color, double Thickness, double Duration, DateTime CreatedAt) : IDebugElement;
        public sealed record DebugCircle(int Id, XYZ Center, double Radius, Color Color, double Thickness, double Duration, DateTime CreatedAt) : IDebugElement;
        public sealed record DebugText(int Id, XYZ Position, string Text, Color Color, double Thickness, double Duration, DateTime CreatedAt) : IDebugElement;
        public sealed record DebugArrow(int Id, XYZ Start, XYZ End, Color Color, double Thickness, double Duration, DateTime CreatedAt) : IDebugElement;

        private static int _nextId = 1;

        private static readonly List<DebugLine> _lines = new();
        private static readonly List<DebugCircle> _circles = new();
        private static readonly List<DebugText> _texts = new();
        private static readonly List<DebugArrow> _arrows = new();

        public static IReadOnlyList<DebugLine> Lines => _lines;
        public static IReadOnlyList<DebugCircle> Circles => _circles;
        public static IReadOnlyList<DebugText> Texts => _texts;
        public static IReadOnlyList<DebugArrow> Arrows => _arrows;

        // id: pass null to add a new element, or an id previously returned
        // from this API to replace that element in place.
        public static int Line(int? id, XYZ start, XYZ end, Color? color = null, double thickness = 2, double duration = -1)
        {
            var entry = new DebugLine(id ?? NextId(), start, end, color ?? Colors.Red, thickness, duration, DateTime.Now);
            Replace(_lines, entry);
            return entry.Id;
        }

        public static int Circle(int? id, XYZ center, double radius, Color? color = null, double thickness = 2, double duration = -1)
        {
            var entry = new DebugCircle(id ?? NextId(), center, radius, color ?? Colors.Yellow, thickness, duration, DateTime.Now);
            Replace(_circles, entry);
            return entry.Id;
        }

        public static int Text(int? id, XYZ position, string text, Color? color = null, double thickness = 12, double duration = -1)
        {
            var entry = new DebugText(id ?? NextId(), position, text, color ?? Colors.White, thickness, duration, DateTime.Now);
            Replace(_texts, entry);
            return entry.Id;
        }

        public static int Arrow(int? id, XYZ start, XYZ end, Color? color = null, double thickness = 2, double duration = -1)
        {
            var entry = new DebugArrow(id ?? NextId(), start, end, color ?? Colors.Red, thickness, duration, DateTime.Now);
            Replace(_arrows, entry);
            return entry.Id;
        }

        // A rectangle outline from start to end (the run's centerline),
        // fixed at the width of 5 lines spaced spacing apart - representing
        // a bundle of conduits sharing one straight trunk line as a single
        // bank, rather than drawing one line per conduit.
        public static void ConduitRun(XYZ start, XYZ end, double spacing = 0.5, Color? color = null, double thickness = 2, double duration = -1)
        {
            var along = end - start;

            if (along.IsZeroLength())
            {
                return;
            }

            const int lineCount = 5;

            var direction = along.Normalize();
            var perpendicular = new XYZ(-direction.Y, direction.X, 0);
            var halfWidth = (lineCount - 1) * spacing / 2.0;

            var sideA = perpendicular.Multiply(-halfWidth);
            var sideB = perpendicular.Multiply(halfWidth);

            Line(null, start + sideA, end + sideA, color, thickness, duration);
            Line(null, start + sideB, end + sideB, color, thickness, duration);
            Line(null, start + sideA, start + sideB, color, thickness, duration);
            Line(null, end + sideA, end + sideB, color, thickness, duration);
        }

        public static void ClearAll()
        {
            _lines.Clear();
            _circles.Clear();
            _texts.Clear();
            _arrows.Clear();
        }

        // Drops anything whose duration has elapsed; -1 means indefinite.
        public static void RemoveExpired(DateTime now)
        {
            RemoveExpiredFrom(_lines, now);
            RemoveExpiredFrom(_circles, now);
            RemoveExpiredFrom(_texts, now);
            RemoveExpiredFrom(_arrows, now);
        }

        private static void Replace<T>(List<T> list, T entry) where T : IDebugElement
        {
            var index = list.FindIndex(e => e.Id == entry.Id);

            if (index >= 0)
            {
                list[index] = entry;
            }
            else
            {
                list.Add(entry);
            }
        }

        private static void RemoveExpiredFrom<T>(List<T> list, DateTime now) where T : IDebugElement
        {
            list.RemoveAll(e => e.Duration >= 0 && now >= e.CreatedAt.AddSeconds(e.Duration));
        }

        private static int NextId() => _nextId++;
    }
}
