using System.Text.Json;
using M2DPlus.Core;

// TODO (docs/research/s1a-worker.md §5): detect and exactly match the host's runtime,
// Rhino build/revision/RhinoCommon hash, engine hash, architecture and protocol schema.
// Rhino 8.36 defaults to .NET 10 here. This net8 skeleton cannot claim worker compatibility
// with that host. Supply/test a matching runtime variant before accepting any scene bytes.
Console.WriteLine(JsonSerializer.Serialize(new
{
    kind = "m2d-plus-worker-identity",
    identity = BuildIdentity.Current,
    capabilities = Array.Empty<string>(),
    engineAvailable = false,
    rhinoLoaded = false
}));
