using System.Security.Cryptography;
using System.Text.Json;
using M2DPlus.Core.Visibility;

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
var input = JsonSerializer.Deserialize<AuditInput>(File.ReadAllText(args[0]))!;
var reference = ExactVisibilityOracle.Solve(input.Faces,input.Edges,input.Tolerance,threads:1);
var expected = VisibilityLoci.Create(input.Edges,reference,input.Tolerance);
var actual = VisibilityLoci.Union(input.Actual,input.Tolerance);
var missing = VisibilityLoci.Compare(expected,actual,input.Tolerance,input.Spacing);
var extra = VisibilityLoci.Compare(actual,expected,input.Tolerance,input.Spacing);
var result = new { passed = missing.Missed == 0 && extra.Missed == 0,
    inputSha256 = Hash(args[0]), executableSha256 = Hash(typeof(AuditInput).Assembly.Location),
    coreSha256 = Hash(typeof(ExactVisibilityOracle).Assembly.Location), reference.FaceTests,
    completePairs = (long)input.Faces.Length * input.Edges.Count(e => e.A.X != e.B.X || e.A.Y != e.B.Y),
    reference.Intervals, referenceLoci = expected, missing, extra };
File.WriteAllText(args[1],JsonSerializer.Serialize(result));
Console.WriteLine($"{Path.GetFileName(args[0])}: missed {missing.Missed}/{extra.Missed}; pairs {reference.FaceTests}");
return result.passed ? 0 : 1;
internal sealed record AuditInput(PlanarFace[] Faces,CandidateEdge[] Edges,Locus[] Actual,double Tolerance,double Spacing);
