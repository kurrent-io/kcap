namespace Capacitor.App.ViewModels.Pages;

/// A work item's slices drawn by what they wait on: boxes left to right in the order they can be finished, with an
/// arrow from each to every slice it waits on. <see cref="Loose"/> are the slices that wait on nothing and that nothing
/// waits on, kept out of the drawing so they don't bury it.
public sealed record WorkItemPageGraph(IReadOnlyList<WorkItemPageGraphNode> Nodes, IReadOnlyList<WorkItemPageGraphEdge> Edges,
        IReadOnlyList<WorkItemPagePart> Loose, double Width, double Height) {
    const double ColumnGap = 70, RowGap = 30, Pad = 10, PortGap = 16;

    public static WorkItemPageGraph Empty { get; } = new([], [], [], 0, 0);

    public static WorkItemPageGraph Of(IReadOnlyList<WorkItemPagePart> slices) {
        var waitedOn = slices.SelectMany(slice => slice.WaitsOn).ToHashSet(StringComparer.Ordinal);
        var drawn = slices.Where(slice => slice.WaitsOn.Count > 0 || waitedOn.Contains(slice.Id)).ToDictionary(slice => slice.Id, StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        // A slice sits one column right of the deepest slice it waits on. Seeded with 0 so a loop ends.
        int Depth(string id) {
            if (depths.TryGetValue(id, out var known)) return known;
            depths[id] = 0;
            var waits = drawn[id].WaitsOn.Where(drawn.ContainsKey).ToList();
            return depths[id] = waits.Count == 0 ? 0 : 1 + waits.Max(Depth);
        }

        var columns = drawn.Values.GroupBy(slice => Depth(slice.Id)).ToList();
        var rows = columns.Select(column => column.Count()).DefaultIfEmpty(0).Max();
        var nodes = columns.SelectMany(column => {
            var top = Pad + (rows - column.Count()) * (WorkItemPageGraphNode.Height + RowGap) / 2;
            return column.Select((slice, row) => new WorkItemPageGraphNode(slice,
                Pad + column.Key * (WorkItemPageGraphNode.Width + ColumnGap), top + row * (WorkItemPageGraphNode.Height + RowGap)));
        }).ToDictionary(node => node.Part.Id, StringComparer.Ordinal);
        var edges = nodes.Values.SelectMany(from => {
            var targets = from.Part.WaitsOn.Where(nodes.ContainsKey).Select(id => nodes[id]).OrderBy(node => node.Y).ToList();
            // Fanned out, but never past the box's own edge.
            var gap = targets.Count < 2 ? 0 : Math.Min(PortGap, (WorkItemPageGraphNode.Height - 16) / (targets.Count - 1));
            return targets.Select((to, i) => new WorkItemPageGraphEdge(from, to, (2 * i - (targets.Count - 1)) * gap / 2));
        });
        return new([.. nodes.Values], [.. edges], [.. slices.Where(slice => !drawn.ContainsKey(slice.Id))],
            nodes.Values.Select(node => node.X + WorkItemPageGraphNode.Width + Pad).DefaultIfEmpty(0).Max(),
            nodes.Values.Select(node => node.Y + WorkItemPageGraphNode.Height + Pad).DefaultIfEmpty(0).Max());
    }
}
