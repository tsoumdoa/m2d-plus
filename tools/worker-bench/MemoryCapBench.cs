using System.Diagnostics;
using System.Text.Json.Nodes;
using M2DPlus.Core;
using M2DPlus.Rhino.Services;
using Rhino;
using Rhino.Runtime.InProcess;

/// <summary>Small-model calibration only: separate reference/export and one-worker processes.</summary>
internal static class MemoryCapBench
{
    internal static void Export(string model, string output)
    {
        Directory.CreateDirectory(output);
        using var core = new RhinoCore(["/nosplash", "/notemplate"], WindowStyle.NoWindow);
        using var document = RhinoDoc.OpenHeadless(model);
        using var cache = new SceneCache(); using var watch = new DocumentWatch(document);
        using var capture = new SceneCapture(document, watch, null, cache);
        while (!capture.Step(TimeSpan.FromSeconds(30))) { }
        using var lease = capture.Accept(); var scene = lease.Value;
        var view = ViewRequest.Parse("SW"); var identity = new RequestIdentity(scene.Revision, view.Revision, "memory", 1);
        File.WriteAllBytes(Path.Combine(output, "scene.json"), WorkerCodec.Capture(scene, default));
        File.WriteAllBytes(Path.Combine(output, "identity.json"), WorkerCodec.Encode(WorkerCodec.Fingerprint()));
        File.WriteAllBytes(Path.Combine(output, "cost.json"), WorkerCodec.Encode(scene.Cost));
        File.WriteAllBytes(Path.Combine(output, "request.json"), WorkerCodec.Encode(new WorkerCodec.Request(identity, 0, view, false)));
        var clock = Stopwatch.StartNew();
        using var batch = NativeDrawing.SolveBorrowedAsync(scene, identity, [view], new()
        { Scheduler = new(8 * WorkerMemoryPolicy.GiB), IsWorker = true }, default, null).GetAwaiter().GetResult();
        var drawing = batch.Views[0]; if (drawing.Error is { } error) throw new InvalidOperationException(error);
        var stable = WorkerCodec.CaptureDrawing(drawing, true);
        File.WriteAllBytes(Path.Combine(output, "reference.json"), stable);
        File.WriteAllBytes(Path.Combine(output, "reference-meta.json"), WorkerCodec.Encode(new
        { model, modelSha256 = WorkerCodec.Hash(File.ReadAllBytes(model)), cost = scene.Cost,
            fingerprint = WorkerCodec.Fingerprint(), seconds = clock.Elapsed.TotalSeconds, hash = WorkerCodec.Hash(stable), phases = drawing.Phases.Seconds }));
        Console.WriteLine($"Reference {Path.GetFileName(model)}: {clock.Elapsed.TotalSeconds:F3}s, {scene.Cost}");
    }

    internal static async Task Worker(string output, string directory, long capGiB)
    {
        var cost = WorkerCodec.Decode<SceneCost>(File.ReadAllBytes(Path.Combine(output, "cost.json")));
        var memory = WorkerMachineMemory.Read();
        var sizing = WorkerMemoryPolicy.Size(cost, 1, 1, memory, capGiB * WorkerMemoryPolicy.GiB);
        if (sizing.Count == 0) throw new InvalidOperationException(sizing.Refusal);
        var expected = WorkerCodec.Decode<WorkerFingerprint>(File.ReadAllBytes(Path.Combine(output, "identity.json")));
        using var client = await WorkerClient.StartAsync(new() { Directory = directory, MemoryBytes = sizing.CapBytes }, expected, default);
        await client.CallAsync("scene", File.ReadAllBytes(Path.Combine(output, "scene.json")), TimeSpan.FromMinutes(2), default);
        var beforeSolve = client.PeakBytes; var clock = Stopwatch.StartNew();
        var reply = WorkerCodec.Decode<WorkerCodec.Reply>(await client.CallAsync("solve",
            File.ReadAllBytes(Path.Combine(output, "request.json")), TimeSpan.FromMinutes(2), default));
        var seconds = clock.Elapsed.TotalSeconds; var peak = client.PeakBytes;
        var node = JsonNode.Parse(reply.Drawing)!;
        var error = node["Values"]!["Error"]?.GetValue<string>();
        var phases = node["Phases"]!.DeepClone();
        node["Values"]!["Seconds"] = 0; node["Diagnostics"] = new JsonArray(); node["Phases"] = new JsonObject();
        var stable = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);
        var matches = stable.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, "reference.json")));
        File.WriteAllBytes(Path.Combine(output, "worker-result.json"), WorkerCodec.Encode(new
        { cost, sizing, memory, seconds, peakCommitBytes = peak, beforeSolvePeakBytes = beforeSolve,
            memoryLimitHit = client.MemoryLimitHit, error, matches, hash = WorkerCodec.Hash(stable), phases, fingerprint = expected }));
        Console.WriteLine($"Worker: {seconds:F3}s; peak {peak / (double)WorkerMemoryPolicy.GiB:F3} GiB; cap {sizing.CapBytes / (double)WorkerMemoryPolicy.GiB:F0}; identical={matches}; {error}");
        if (!matches || error is not null) throw new InvalidOperationException("Worker reference mismatch or solve failure");
    }
}
