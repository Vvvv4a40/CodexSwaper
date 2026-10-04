using System.Diagnostics;
using System.Text.Json;
using CodexProfileOverlay;
using CodexProfileOverlay.Core.Services;

// Synthetic data only: no native process enumeration, windows, credentials, profiles or CLI.
const int Repetitions = 10;
const int Samples = 7;
DateTime started = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
var cases = new List<object>();
foreach ((int count, int selectedCount) in new[] { (1000, 100), (10000, 500) })
{
    var captured = Enumerable.Range(1, count).Select(id => new CapturedDesktopProcess(
        new DesktopProcessSnapshot(id, id == count - selectedCount ? 0 : count - selectedCount, 1, started.AddTicks(id)),
        "helper.exe")).ToArray();
    var selected = DesktopProcessSelector.SelectTree(captured.Select(item => item.Identity), [count - selectedCount], 1, -1);
    // Compare the old repeated Any/First expression to the current single index + TryGetValue.
    long LinearLookup()
    {
        long total = 0;
        foreach (var identity in selected)
        {
            if (!captured.Any(item => item.Identity.ProcessId == identity.ProcessId)) { continue; }
            total += captured.First(item => item.Identity.ProcessId == identity.ProcessId).ExecutableName.Length;
        }
        return total;
    }
    long IndexedLookup()
    {
        var capturedById = captured.ToDictionary(item => item.Identity.ProcessId);
        long total = 0;
        foreach (var identity in selected)
        {
            if (capturedById.TryGetValue(identity.ProcessId, out var item)) { total += item.ExecutableName.Length; }
        }
        return total;
    }
    if (LinearLookup() != IndexedLookup()) { throw new InvalidOperationException("Lookup outputs differ."); }
    Measurement baseline = Measure(LinearLookup);
    Measurement optimized = Measure(IndexedLookup);
    cases.Add(new
    {
        scenario = "shutdown snapshot identity/name lookup (synthetic)",
        processRows = count,
        selectedRows = selected.Count,
        baseline,
        optimized,
        medianSpeedRatio = baseline.MedianMilliseconds / optimized.MedianMilliseconds,
    });
}

string installedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps",
    "OpenAI.Codex_26.930.3930.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe");
int queries = 0;
var cache = new DesktopExecutableIdentityCache();
for (int tick = 0; tick < 800; tick++)
{
    if (!cache.GetOrVerify(10, started, "ChatGPT", () => { queries++; return installedPath; }))
    {
        throw new InvalidOperationException("Synthetic official identity was rejected.");
    }
}
bool reusedPidRejected = !cache.GetOrVerify(10, started.AddTicks(1), "ChatGPT", () => @"C:\untrusted\ChatGPT.exe");
if (!reusedPidRejected) { throw new InvalidOperationException("PID reuse inherited the cache decision."); }

Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = Environment.Version.ToString(),
    operatingSystem = Environment.OSVersion.VersionString,
    timestampUtc = DateTimeOffset.UtcNow,
    scope = "Microbenchmarks of synthetic snapshots and the production executable cache; not full application timings.",
    accessesCredentials = false,
    enumeratesOrTerminatesProcesses = false,
    measurements = cases,
    executablePathChecksOver800KnownIdentityPolls = new { baseline = 800, optimized = queries, reusedPidRejected },
}, new JsonSerializerOptions { WriteIndented = true }));

Measurement Measure(Func<long> action)
{
    for (int warmup = 0; warmup < 5; warmup++) { _ = action(); }
    var durations = new List<double>();
    var allocations = new List<long>();
    long checksum = 0;
    for (int sample = 0; sample < Samples; sample++)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long clock = Stopwatch.GetTimestamp();
        for (int repeat = 0; repeat < Repetitions; repeat++) { checksum += action(); }
        durations.Add(Stopwatch.GetElapsedTime(clock).TotalMilliseconds / Repetitions);
        allocations.Add((GC.GetAllocatedBytesForCurrentThread() - before) / Repetitions);
    }
    durations.Sort();
    allocations.Sort();
    return new Measurement(durations[Samples / 2], allocations[Samples / 2], checksum);
}

internal sealed record Measurement(double MedianMilliseconds, long MedianAllocatedBytes, long Checksum);
