using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using EntJoy.ECS;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;
using RelationshipValidation;

var allowed = new HashSet<string>(new[] { "--mode", "--backend", "--out", "--size", "--steps", "--layout", "--degree" });
var seen = new HashSet<string>();
for (int i = 0; i < args.Length; i += 2)
    if (i + 1 >= args.Length || !allowed.Contains(args[i]) || !seen.Add(args[i]))
        throw new ArgumentException("Options must be unique supported names followed by a value.");
string Read(string name, string fallback) { int index = Array.IndexOf(args, name); return index >= 0 ? args[index + 1] : fallback; }
string mode = Read("--mode", "model"), backend = Read("--backend", "managed"), output = Read("--out", "relationship-results.jsonl");
int size = int.Parse(Read("--size", "10000")), steps = int.Parse(Read("--steps", "10000"));
string layout = Read("--layout", "both");
int degree = int.Parse(Read("--degree", "8"));
if (backend is not ("managed" or "native") || layout is not ("list" or "fixed" or "both")
    || size < 3 || steps < 1 || degree < 1 || degree > 8
    || mode is not ("model" or "steady" or "bench" or "multi-stress" or "multi-steady"))
    throw new ArgumentException("Require backend=managed|native, layout=list|fixed|both, size>=3, steps>=1 and degree=1..8.");
if (backend == "managed")
{
    typeof(JobScheduler).GetProperty("UseNative", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
    typeof(NativeJobScheduler).GetProperty("UseFallback", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(null, true);
    ManagedJobScheduler.Initialize(2);
}
else { JobScheduler.Initialize(2); if (!JobScheduler.IsNative) throw new Exception("Native backend was requested but is unavailable."); }
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
using var writer = new StreamWriter(output, append: false) { AutoFlush = true };
void Emit(object value) { string json = JsonSerializer.Serialize(value); writer.WriteLine(json); Console.WriteLine(json); }
string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
int actualWorkers = backend == "managed"
    ? (int)typeof(ManagedJobScheduler).GetField("_workerCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
    : JobScheduler.WorkerCount;
Emit(new { kind = "environment", mode, backend, actual_backend = JobScheduler.IsNative ? "native" : "managed", size, steps, layout, degree, runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription, processors = Environment.ProcessorCount, workers = actualWorkers,
    scheduler_reported_workers = JobScheduler.WorkerCount,
    cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), stopwatch_frequency = Stopwatch.Frequency,
    ecs_sha256 = Hash(typeof(World).Assembly.Location),
    dll_sha256 = File.Exists(Path.Combine(AppContext.BaseDirectory, "NativeDll.dll"))
        ? Hash(Path.Combine(AppContext.BaseDirectory, "NativeDll.dll")) : null });
try
{
    if (mode == "model") foreach (int seed in new[] { 731, 1039, 42069 }) new EdgeModel(seed, 128).Run(steps, Emit);
    else if (mode == "steady") SteadyScenario.Run(steps, Emit);
    else if (mode == "bench") RelationCosts.Run(size, Emit);
    else if (mode == "multi-stress") MultiPressure.Run(size, degree, layout, Emit);
    else if (mode == "multi-steady") MultiSteady.Run(size, degree, steps, layout, Emit);
    else throw new ArgumentException("Mode must be model, steady, bench, multi-stress or multi-steady.");
    Emit(new { kind = "complete", mode, backend, success = true });
}
finally { if (backend == "managed") ManagedJobScheduler.Shutdown(); else JobScheduler.Shutdown(); }
