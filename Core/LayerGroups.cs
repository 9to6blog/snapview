using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SnapView.Core
{
    internal static class LayerGroups
    {
        internal static List<Annotation> Members(IReadOnlyList<Annotation> all, Annotation a)
            => a.GroupId == null ? new List<Annotation> { a } : all.Where(x => x.GroupId == a.GroupId).ToList();

        internal static List<List<Annotation>> Units(IReadOnlyList<Annotation> all, IReadOnlyList<Annotation> selected, bool keepGroups = true)
        {
            var result = new List<List<Annotation>>();
            var seen = new HashSet<Annotation>();
            foreach (Annotation a in selected)
            {
                if (!seen.Add(a)) continue;
                var members = keepGroups ? Members(all, a) : new List<Annotation> { a };
                if (!members.All(selected.Contains)) members = new List<Annotation> { a };
                foreach (Annotation m in members) seen.Add(m);
                if (members.Any(m => m.Locked)) continue;
                result.Add(members);
            }
            return result;
        }

        internal static List<Annotation> Duplicate(IReadOnlyList<Annotation> items)
        {
            var clones = ArrangeTools.CloneAll(items);
            var ids = new Dictionary<string, string>();
            foreach (Annotation a in clones)
            {
                if (a.GroupId == null) continue;
                if (!ids.TryGetValue(a.GroupId, out string? id)) ids[a.GroupId] = id = Guid.NewGuid().ToString("N");
                a.GroupId = id;
            }
            return clones;
        }

        internal static void Align(IReadOnlyList<List<Annotation>> units, AlignMode mode, Rect? background = null)
        {
            if (units.Count == 0) return;
            Rect reference = background ?? ArrangeTools.Union(units.SelectMany(x => x).ToList());
            foreach (var unit in units)
            {
                Rect b = ArrangeTools.Union(unit);
                Vector d = mode switch
                {
                    AlignMode.Left => new Vector(reference.Left - b.Left, 0),
                    AlignMode.CenterH => new Vector(reference.X + reference.Width / 2 - b.X - b.Width / 2, 0),
                    AlignMode.Right => new Vector(reference.Right - b.Right, 0),
                    AlignMode.Top => new Vector(0, reference.Top - b.Top),
                    AlignMode.CenterV => new Vector(0, reference.Y + reference.Height / 2 - b.Y - b.Height / 2),
                    _ => new Vector(0, reference.Bottom - b.Bottom)
                };
                foreach (Annotation a in unit) a.Move(d);
            }
        }

        internal static void Distribute(IReadOnlyList<List<Annotation>> units, bool horizontal)
        {
            if (units.Count < 3) return;
            double Center(List<Annotation> unit)
            {
                Rect b = ArrangeTools.Union(unit);
                return horizontal ? b.X + b.Width / 2 : b.Y + b.Height / 2;
            }
            var sorted = units.OrderBy(Center).ToList();
            double first = Center(sorted[0]), step = (Center(sorted[^1]) - first) / (sorted.Count - 1);
            for (int i = 1; i + 1 < sorted.Count; i++)
            {
                double delta = first + step * i - Center(sorted[i]);
                foreach (Annotation a in sorted[i]) a.Move(horizontal ? new Vector(delta, 0) : new Vector(0, delta));
            }
        }
    }
}
