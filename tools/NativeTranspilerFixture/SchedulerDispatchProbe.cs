using System.Diagnostics;
using System.Globalization;
using System.Text;
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

[NativeTranspile]
public struct DispatchSerialJob : IJob
{
    public NativeArray<int> Proof;
    public void Execute() { Proof[0] += 1; }
}

[NativeTranspile]
public struct DispatchParallelJob : IJobParallelFor
{
    public NativeArray<int> Proof;
    public int JobIndex;
    public void Execute(int index) { if (index == 0) Proof[JobIndex] += 1; }
}

[NativeTranspile]
public struct DispatchStampJob : IJobParallelFor
{
    public NativeArray<int> Stamp;
    public int JobIndex, Elements, Epoch;
    public void Execute(int index) { Stamp[JobIndex * Elements + index] = Epoch; }
}

// Mirrors Unity BattleBenchDispatchFloor: the timer covers submission and completion;
// per-job counters and full-traversal checks are read outside the timed interval.
public static class SchedulerDispatchProbe
{
    public static void Run(string path)
    {
        var csv = new StringBuilder("probe,arm,metric,value\n");
        var iterations = new StringBuilder("arm,rep,ms\n");
        var proof = new NativeArray<int>(1024);
        var stamp = new NativeArray<int>(2 * 1024 * 1024);
        var handles = new JobHandle[1024];
        int epoch = 0;
        try
        {
            Measure("empty_serial_ijob", 1, 1, 0,
                () => new DispatchSerialJob { Proof = proof }.Schedule().Complete());
            foreach (int jobs in new[] { 1, 100, 1000 })
                Parallel($"empty_parallelfor_{jobs}", jobs, 1, 1);
            foreach (int jobs in new[] { 1, 100, 1000 })
                foreach (int batch in new[] { 1, 64, 1024 })
                    Parallel($"empty_parallelfor_jobs{jobs}_e1024_batch{batch}", jobs, 1024, batch);
            foreach (int batch in new[] { 1, 64, 1024 })
                Parallel($"empty_parallelfor_batch_sweep_batch{batch}", 1, 1000000, batch);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, csv.ToString());
            File.WriteAllText(Path.ChangeExtension(path, ".iters.csv"), iterations.ToString());
        }
        finally { proof.Dispose(); stamp.Dispose(); }

        void Parallel(string arm, int jobs, int elements, int batch)
        {
            epoch++;
            for (int j = 0; j < jobs; j++)
                handles[j] = new DispatchStampJob { Stamp = stamp, JobIndex = j, Elements = elements, Epoch = epoch }.Schedule(elements, batch);
            JobHandle.CombineDependencies(handles).Complete();
            for (int i = 0; i < jobs * elements; i++)
                if (stamp[i] != epoch) throw new InvalidOperationException($"Traversal proof failed: {arm} index={i}");
            Measure(arm, jobs, elements, batch, () => {
                for (int j = 0; j < jobs; j++)
                    handles[j] = new DispatchParallelJob { Proof = proof, JobIndex = j }.Schedule(elements, batch);
                JobHandle.CombineDependencies(handles).Complete();
            });
        }

        void Measure(string arm, int jobs, int elements, int batch, Action body)
        {
            const int warmup = 20;
            int reps = (int)Math.Min(100, Math.Max(1, 80000000L / ((long)jobs * elements)));
            for (int i = 0; i < warmup; i++) body();
            var values = new double[reps];
            for (int i = 0; i < reps; i++)
            {
                long before = Sum();
                long start = Stopwatch.GetTimestamp();
                body();
                values[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (Sum() - before != jobs) throw new InvalidOperationException($"Execution proof failed: {arm}");
                iterations.Append(arm).Append(',').Append(i).Append(',').Append(values[i].ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
            }
            Array.Sort(values);
            double median = (values[(reps - 1) / 2] + values[reps / 2]) * 0.5;
            Row("median_ms", median); Row("min_ms", values[0]); Row("mean_ms", values.Average());
            Row("us_per_job", median * 1000 / jobs); Row("jobs", jobs); Row("elems_per_job", elements);
            Row("batch", batch); Row("reps", reps); Row("proof_fail_reps", 0);
            Console.WriteLine($"[JOBSYSTEM] {arm} median_ms={median:F6} jobs={jobs} proof=OK");

            long Sum() { long count = 0; for (int j = 0; j < jobs; j++) count += proof[j]; return count; }
            void Row(string metric, double value) => csv.Append("M4DISP,").Append(arm).Append(',').Append(metric).Append(',')
                .Append(value.ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }
    }
}
