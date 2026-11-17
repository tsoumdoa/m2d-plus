namespace M2DPlus.Core.Placement;

public readonly record struct Vector3
{
    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public Vector3(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) throw new ArgumentException("Coordinates must be finite.");
        X = x; Y = y; Z = z;
    }
    public static Vector3 Zero => new(0, 0, 0);
    public static Vector3 UnitX => new(1, 0, 0);
    public static Vector3 UnitY => new(0, 1, 0);
    public static Vector3 UnitZ => new(0, 0, 1);
    public double Length
    {
        get
        {
            var scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
            return scale == 0 ? 0 : scale * Math.Sqrt(Math.Pow(X / scale, 2) + Math.Pow(Y / scale, 2) + Math.Pow(Z / scale, 2));
        }
    }
    public double Dot(Vector3 other) => X * other.X + Y * other.Y + Z * other.Z;
    public Vector3 Cross(Vector3 other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
    public Vector3 Unit()
    {
        var scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
        if (scale == 0) throw new ArgumentException("Axis must be nonzero.");
        var scaled = new Vector3(X / scale, Y / scale, Z / scale);
        return scaled / scaled.Length;
    }
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator -(Vector3 a) => a * -1;
    public static Vector3 operator *(Vector3 a, double factor) => new(a.X * factor, a.Y * factor, a.Z * factor);
    public static Vector3 operator /(Vector3 a, double factor) => a * (1 / factor);
}

/// <summary>Orthonormal right-handed frame. Coordinates may represent points or vectors in model units.</summary>
public sealed record Plane3
{
    public Vector3 Origin { get; }
    public Vector3 X { get; }
    public Vector3 Y { get; }
    public Vector3 Z { get; }
    public static Plane3 WorldXY { get; } = new(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
    public Plane3(Vector3 origin, Vector3 x, Vector3 y)
    {
        x = x.Unit(); y = y.Unit();
        if (Math.Abs(x.Dot(y)) > 1e-8) throw new ArgumentException("Plane axes must be perpendicular.");
        Origin = origin; X = x; Z = x.Cross(y).Unit(); Y = Z.Cross(X).Unit();
    }
    public Vector3 At(double u, double v, double w = 0) => Origin + X * u + Y * v + Z * w;
    public Vector3 Coordinates(Vector3 point) { var delta = point - Origin; return new(delta.Dot(X), delta.Dot(Y), delta.Dot(Z)); }
    public Vector3 Project(Vector3 point) => point - Z * (point - Origin).Dot(Z);
    public Plane3 AtOrigin(Vector3 origin) => new(origin, X, Y);
    public Plane3 FlipNormal() => new(Origin, -X, Y);
}

/// <summary>Frame in anchor coordinates, supplied from crop/input bounds, never from output geometry.</summary>
public sealed record Frame2
{
    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Frame2(double minX, double minY, double maxX, double maxY)
    {
        if (new[] { minX, minY, maxX, maxY }.Any(v => !double.IsFinite(v)) || minX > maxX || minY > maxY ||
            !double.IsFinite(maxX - minX) || !double.IsFinite(maxY - minY)) throw new ArgumentException("Invalid frame bounds.");
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    /// <summary>Project input bounding-box corners already conservatively restricted to the view volume.</summary>
    public static Frame2 FromInputBounds(Plane3 plane, IEnumerable<Vector3> corners)
    {
        var points = corners.Select(plane.Coordinates).ToArray();
        if (points.Length == 0) throw new ArgumentException("An empty scene needs a host-provided fallback frame.", nameof(corners));
        return new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }
}

public sealed record RigidPlacement(Plane3 Source, Plane3 Target)
{
    public Vector3 Map(Vector3 point) { var p = Source.Coordinates(point); return Target.At(p.X, p.Y, p.Z); }
    public Vector3 MapVector(Vector3 vector) => Target.X * vector.Dot(Source.X) + Target.Y * vector.Dot(Source.Y) + Target.Z * vector.Dot(Source.Z);
    public RigidPlacement Inverse() => new(Target, Source);
}
