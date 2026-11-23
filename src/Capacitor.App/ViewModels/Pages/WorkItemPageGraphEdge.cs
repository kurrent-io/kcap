using System.Globalization;
using Avalonia.Media;

namespace Capacitor.App.ViewModels.Pages;

/// An arrow from a slice's left edge to the right edge of one it waits on. <paramref name="Dy"/> fans out the arrows
/// of a slice that waits on several, so they don't leave from one point.
public sealed record WorkItemPageGraphEdge(WorkItemPageGraphNode From, WorkItemPageGraphNode To, double Dy) {
    double StartY => From.Y + WorkItemPageGraphNode.Height / 2 + Dy;
    double EndX => To.X + WorkItemPageGraphNode.Width;
    double EndY => To.Y + WorkItemPageGraphNode.Height / 2;

    public Geometry Curve {
        get {
            var mid = (From.X + EndX) / 2;
            return Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M{From.X},{StartY} C{mid},{StartY} {mid},{EndY} {EndX},{EndY}"));
        }
    }

    public Geometry Head => Geometry.Parse(string.Create(CultureInfo.InvariantCulture, $"M{EndX},{EndY} l8,-4 v8 z"));
    public string Tip => $"{From.Part.Title} waits on {To.Part.Title}";
}
