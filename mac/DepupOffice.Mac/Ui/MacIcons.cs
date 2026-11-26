using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace DepupOffice.Mac;

/// <summary>
/// Ikon DepUp Office yang SAMA PERSIS dengan Android/iOS/Windows: path vektor res/drawable/ic_*.xml (MacIcons.g.cs,
/// dibuat tools/icons/gen_icons.py) digambar sebagai Path Avalonia di dalam Viewbox — tajam di layar Retina.
/// </summary>
internal static partial class MacIcons
{
    internal sealed record P(string D, bool EvenOdd, string? Color, float[] M);
    internal sealed record Vec(float W, float H, P[] Paths);

    public static bool Has(string name) => Data.ContainsKey(name);

    /// <summary>Kontrol ikon berukuran <paramref name="size"/> dengan warna <paramref name="tint"/>.</summary>
    public static Control Create(string name, double size, IBrush tint)
    {
        if (!Data.TryGetValue(name, out var v)) return new Border { Width = size, Height = size };
        var canvas = new Canvas { Width = v.W, Height = v.H };
        foreach (var p in v.Paths)
        {
            var geo = Geometry.Parse((p.EvenOdd ? "F0 " : "F1 ") + p.D);
            if (p.M is not [1, 0, 0, 1, 0, 0])
                geo.Transform = new MatrixTransform(new Matrix(p.M[0], p.M[1], p.M[2], p.M[3], p.M[4], p.M[5]));
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = geo,
                Fill = p.Color is { } hex ? new SolidColorBrush(Color.Parse(hex)) : tint,
            });
        }
        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = canvas, IsHitTestVisible = false };
    }
}
