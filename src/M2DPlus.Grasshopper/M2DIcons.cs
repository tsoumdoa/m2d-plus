using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace M2DPlus.Grasshopper;

/// <summary>White glyphs on the shared #181E1A tile. Kept independent of GH for Generate-Icons.ps1.</summary>
public static class M2DIcons
{
    public static Bitmap Drawing(int size = 24) => Render(size, 24, 5.25f, g =>
    {
        using var edge = new Pen(Color.White, 1.4f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var hidden = new Pen(Color.FromArgb(128, Color.White), 1) { DashPattern = new[] { 2f, 1.8f } };
        // Cube viewed down its near corner: truncate that corner with a solid triangular cut face.
        g.DrawLines(hidden, Points(4.5f, 7.5f, 12, 11.5f, 19.5f, 7.5f));
        g.DrawLine(hidden, 12, 11.5f, 12, 20);
        g.DrawPolygon(edge, Points(12, 3.5f, 19.5f, 7.5f, 19.5f, 16, 12, 20, 4.5f, 16, 4.5f, 7.5f));
        g.DrawLines(edge, Points(4.5f, 7.5f, 8.5f, 9.7f));
        g.DrawLines(edge, Points(19.5f, 7.5f, 15.5f, 9.7f));
        g.DrawLine(edge, 12, 20, 12, 15.5f);
        using var ink = new SolidBrush(Color.White);
        g.FillPolygon(ink, Points(8.5f, 9.7f, 15.5f, 9.7f, 12, 15.5f));
    });

    public static Bitmap Logo(int size = 64) => Render(size, 64, 14, g =>
    {
        using var pen = new Pen(Color.White, 6) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, Points(12, 45, 12, 20, 24, 35, 36, 20, 36, 45));
        using var ink = new SolidBrush(Color.White);
        g.FillPolygon(ink, Points(46, 24, 52, 24, 52, 30, 58, 30, 58, 36, 52, 36, 52, 42, 46, 42, 46, 36, 40, 36, 40, 30, 46, 30));
    });

    private static Bitmap Render(int size, float unit, float radius, Action<Graphics> glyph)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / unit, size / unit);
        using var tile = new GraphicsPath();
        var d = radius * 2;
        tile.AddArc(0, 0, d, d, 180, 90);
        tile.AddArc(unit - d, 0, d, d, 270, 90);
        tile.AddArc(unit - d, unit - d, d, d, 0, 90);
        tile.AddArc(0, unit - d, d, d, 90, 90);
        tile.CloseFigure();
        using var background = new SolidBrush(Color.FromArgb(24, 30, 26));
        g.FillPath(background, tile);
        glyph(g);
        return bitmap;
    }

    private static PointF[] Points(params float[] coordinates)
    {
        var points = new PointF[coordinates.Length / 2];
        for (var i = 0; i < points.Length; i++) points[i] = new PointF(coordinates[i * 2], coordinates[i * 2 + 1]);
        return points;
    }
}
