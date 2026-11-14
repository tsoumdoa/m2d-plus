using System.Collections.Immutable;
using System.Globalization;
using M2DPlus.Core.Placement;

namespace M2DPlus.Core.Bake;

public sealed record DrawingNameRequest(ViewSourceKind Source, Plane3 Plane, bool IsCut,
    string? SourceName = null, string? OverrideName = null, int SectionNumber = 1);

public static class BakeNames
{
    public static string Category(LineClass lineClass) => lineClass switch
    {
        LineClass.Visible or LineClass.Profile => "Visible", LineClass.Hidden => "Hidden", LineClass.Beyond => "Beyond",
        LineClass.FarClip => "Far clip", LineClass.Cut => "Cut", LineClass.Hatch => "Hatch", LineClass.Unresolved => "Unresolved",
        _ => throw new ArgumentOutOfRangeException(nameof(lineClass))
    };

    public static string LayerPath(string drawing, LineClass lineClass, string? sourceLayerPath = null)
    {
        ValidateSegment(drawing);
        if (sourceLayerPath is not null && sourceLayerPath.Length != 0)
            foreach (var segment in sourceLayerPath.Split("::", StringSplitOptions.None)) ValidateSegment(segment);
        return $"M2D+::{drawing}::{Category(lineClass)}" + (string.IsNullOrEmpty(sourceLayerPath) ? "" : $"::{sourceLayerPath}");
    }

    public static string Infer(DrawingNameRequest request, DocumentUnits units)
    {
        _ = Settings.MetresPerUnit(units);
        if (request.OverrideName is not null) { ValidateSegment(request.OverrideName); return request.OverrideName; }
        if (request.Source is ViewSourceKind.NamedView or ViewSourceKind.ClippingPlane && !string.IsNullOrWhiteSpace(request.SourceName))
        { ValidateSegment(request.SourceName); return request.SourceName; }
        if (request.SectionNumber < 1) throw new ArgumentOutOfRangeException(nameof(request));
        var normal = request.Plane.Z;
        if (Math.Abs(normal.Z) > 1 - 1e-8) return $"Plan {FormatElevation(request.Plane.Origin.Z, units)}";
        if (request.IsCut) return $"Section {request.SectionNumber}";
        if (Math.Abs(normal.Z) < 1e-8)
        {
            var facing = Math.Abs(normal.X) > Math.Abs(normal.Y) ? normal.X > 0 ? "East" : "West" : normal.Y > 0 ? "North" : "South";
            return $"Elevation {facing}";
        }
        return $"Axo {(normal.Y >= 0 ? "N" : "S")}{(normal.X >= 0 ? "E" : "W")}";
    }

    /// <summary>Metres use signed two decimals (Plan +1.20); other units include an explicit suffix.</summary>
    public static string FormatElevation(double value, DocumentUnits units)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var decimals = units == DocumentUnits.Millimetres ? 0 : 2;
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        var suffix = units switch { DocumentUnits.Metres => "", DocumentUnits.Millimetres => " mm", DocumentUnits.Centimetres => " cm",
            DocumentUnits.Kilometres => " km", DocumentUnits.Inches => " in", DocumentUnits.Feet => " ft", _ => throw new ArgumentOutOfRangeException(nameof(units)) };
        return (rounded < 0 ? "-" : "+") + Math.Abs(rounded).ToString($"F{decimals}", CultureInfo.InvariantCulture) + suffix;
    }

    public static ImmutableArray<string> Unique(IEnumerable<string> names, IEnumerable<string>? reserved = null)
    {
        var used = new HashSet<string>(reserved ?? [], StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var name in names)
        {
            ValidateSegment(name);
            var candidate = name; var suffix = 2;
            while (!used.Add(candidate)) candidate = $"{name} ({suffix++})";
            result.Add(candidate);
        }
        return result.ToImmutable();
    }

    internal static void ValidateSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("::", StringComparison.Ordinal) || name.Any(char.IsControl))
            throw new ArgumentException("Layer and drawing names must be nonempty and contain no :: or control characters.", nameof(name));
    }
}
