using Rhino.Geometry;

namespace M2DPlus.Rhino.Services;

internal sealed record VisibilityInput(GeometryBase Geometry, PreparedLeaf Source, bool Far = false);
