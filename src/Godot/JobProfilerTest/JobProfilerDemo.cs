using Godot;
using System;
using System.Diagnostics;

/// <summary>
/// Job Profiler 完整演示。
/// 挂载到任意 Node 后：
///   - 按 F3 切换 Profiler 面板
///   - 按 F1 一帧内运行所有 6 种 Job，展示 Worker 分配
///   - 面板 Enable 后每物理帧自动记录一帧（Frame 编号递增）
///
/// 使用方法:
///   1. 挂载到根节点
///   2. 运行 -> F3 打开面板 -> Enable
///   3. 按 F1 运行 Job，面板显示 Worker 数据
///   4. 按 || 暂停，用 < > 按钮或滑动条浏览历史帧
/// </summary>
public partial class JobProfilerDemo : Node
{
    [Export] private Key _togglePanelKey = Key.F3;
    [Export] private Key _runJobsKey = Key.F1;

    private Control _profilerPanel;
    private Label _hintLabel;
    private Button _runButton;
    private bool _profilerLoaded;
    private Random _rng = new(42);
    private int _callCount;

    // 预缓存数组（避免 GC）
    private float[] _bigArray;
    private int[] _intArray;

    public override void _Ready()
    {
        // 预分配数组
        _bigArray = new float[1_500_000];
        _intArray = new int[300_000];
        for (int i = 0; i < _intArray.Length; i++) _intArray[i] = i;

        // 1. 顶部提示文字（固定 24px 高）
        _hintLabel = new Label
        {
            Text = "[F3] Profiler  |  [F1] Run All Jobs  |  [||] pause to browse history\nPanel [Enable]d: Frame auto-increments each physics tick. Press F1 to generate job data.",
            AnchorLeft = 0, AnchorRight = 1,
            AnchorTop = 0, AnchorBottom = 0,
            OffsetTop = 4, OffsetBottom = -24,
        };
        _hintLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.5f));
        AddChild(_hintLabel);

        // 2. 底部按钮（初始在底部，面板加载后通过 F3 切换位置）
        _runButton = new Button
        {
            Text = ">  Run All Jobs",
            AnchorLeft = 0, AnchorRight = 0,
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 10, OffsetRight = 140,
            OffsetTop = 0, OffsetBottom = -10,  // 面板隐藏时距底部 10px
        };
        _runButton.Pressed += RunAllJobs;
        _runButton.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.5f));
        AddChild(_runButton);

        // 3. 最后加载面板（渲染在最顶层）
        LoadPanel();

        GD.Print("================================================");
        GD.Print("  Job Profiler Demo 已启动");
        GD.Print("  [F3] 打开 Profiler 面板");
        GD.Print("  [F1] 一帧内运行全部 6 种 Job");
        GD.Print("  面板 Enable 后每物理帧自动记录一帧");
        GD.Print("================================================");
    }

    private void LoadPanel()
    {
        _profilerPanel = new JobProfilerPanel();
        _profilerPanel.Visible = false;
        AddChild(_profilerPanel);
        _profilerLoaded = true;
        GD.Print("[Profiler] 面板已直接创建");
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

        switch (key.Keycode)
        {
            case Key.F3:
                if (_profilerLoaded)
                {
                    _profilerPanel.Visible = !_profilerPanel.Visible;
                    // 面板显示时按钮移到面板上方，隐藏时回到底部
                    _runButton.OffsetBottom = _profilerPanel.Visible ? -210 : -10;
                    GD.Print($"[Profiler] 面板 {(_profilerPanel.Visible ? "显示" : "隐藏")}");
                }
                break;
            case Key.F1:
                RunAllJobs();
                break;
        }
    }

    /// <summary>
    /// 一帧内运行所有 6 种 Job，让每个 Worker 上出现多个不同的 Job 条目。
    /// 控制台输出各 Job 的耗时/线程分配/分片数。
    /// </summary>
    private void RunAllJobs()
    {
        _callCount++;

        var handles = new NativeJobHandle[6];
        var jobSizes = new int[6]; // 每个 Job 的总工作量（元素数或迭代次数）

        // 1. 重型数据并行
        {
            int size = 800_000 + (_callCount % 4) * 200_000;
            var b1 = new float[size];
            var b2 = new float[size];
            for (int i = 0; i < size; i++) b1[i] = (float)_rng.NextDouble() * 100f;
            var job = new DemoHeavyJob { Input = b1, Output = b2 };
            handles[0] = NativeJobScheduler.ScheduleParallelFor(ref job, size, 32768);
            jobSizes[0] = size;
        }

        // 2-3. 中型 ParallelFor ×2
        for (int k = 0; k < 2; k++)
        {
            int sz = 400_000 + k * 300_000;
            var d = new int[sz];
            Array.Copy(_intArray, 0, d, 0, Math.Min(sz, _intArray.Length));
            var j = new DemoMathJob { Data = d, Seed = k * 100 };
            int batch = (k == 0) ? 16384 : 65536;
            handles[1 + k] = NativeJobScheduler.ScheduleParallelFor(ref j, sz, batch);
            jobSizes[1 + k] = sz;
        }

        // 4. 轻量 ParallelFor
        {
            int sz = 250_000;
            var d = new int[sz];
            Array.Copy(_intArray, 0, d, 0, Math.Min(sz, _intArray.Length));
            var j = new DemoLightJob2 { Data = d, Factor = _callCount % 10 + 1 };
            handles[3] = NativeJobScheduler.ScheduleParallelFor(ref j, sz, 8192);
            jobSizes[3] = sz;
        }

        // 5. 单线程 IJob
        {
            var j1 = new DemoIJob1 { Iterations = 500_000 + _callCount * 100 };
            handles[4] = NativeJobScheduler.Schedule(ref j1);
            jobSizes[4] = j1.Iterations;
        }

        // 6. 单线程 IJob
        {
            var j2 = new DemoIJob2 { Iterations = 300_000 + _callCount * 80 };
            handles[5] = NativeJobScheduler.Schedule(ref j2);
            jobSizes[5] = j2.Iterations;
        }

        // 等待全部完成
        var swTotal = Stopwatch.StartNew();
        for (int i = 0; i < handles.Length; i++)
            NativeJobScheduler.Complete(ref handles[i]);
        swTotal.Stop();

        // 刷新稳定副本：从 native/C# 缓冲读取最新数据
        // 面板的 _PhysicsProcess 会在同一物理帧稍后自动读取
        JobProfiler.PullFrameData();

        var agg = JobProfiler.AggregateByJob();
        var byThread = JobProfiler.AggregateJobsByThread();
        int usedWorkerCount = byThread.Length;
        int uniqueJobCount = agg.Length;

        // 控制台输出
        GD.PrintRich($"[color=yellow]========================================================[/color]");
        GD.PrintRich($"[color=cyan]Job Batch [{_callCount}] - 物理帧 EngineTick {Engine.GetPhysicsFrames()}[/color]");
        GD.PrintRich($"  共 {uniqueJobCount} 种 Job，分配到 {usedWorkerCount} 个 Worker 线程：");
        for (int i = 0; i < agg.Length; i++)
        {
            var j = agg[i];
            // 估算分片数
            int splitCount = 1;
            if (j.JobName == "DemoHeavyJob") splitCount = jobSizes[0] / 32768;
            else if (j.JobName == "DemoMathJob_0") splitCount = jobSizes[1] / 16384;
            else if (j.JobName == "DemoMathJob_1") splitCount = jobSizes[2] / 65536;
            else if (j.JobName == "DemoLightJob2") splitCount = jobSizes[3] / 8192;

            int threadSpan = j.MaxThreadIndex - j.MinThreadIndex + 1;
            GD.PrintRich(
                $"  [color=#88ccff]{j.JobName,-16}[/color]  " +
                $"耗时 {j.TotalMs,7:F2}ms  " +
                $"调用 {j.CallCount,4} 次  " +
                $"分 {splitCount,4} 片  " +
                $"横跨 W{j.MinThreadIndex}~W{j.MaxThreadIndex} ({threadSpan} 个 Worker)");
        }
        GD.PrintRich($"  [color=#88ff88]总耗时: {swTotal.Elapsed.TotalMilliseconds:F2}ms | " +
            $"物理帧: {Engine.GetPhysicsFrames()} | " +
            $"Worker: {usedWorkerCount} | " +
            $"Job 条目: {JobProfiler.CurrentFrameEntryCount}[/color]");
        GD.PrintRich($"[color=yellow]========================================================[/color]");
    }
}

// ===================== Job 类型 =====================

public struct DemoHeavyJob : IJobParallelFor
{
    public float[] Input;
    public float[] Output;
    public void Execute(int i)
    {
        float x = Input[i];
        Output[i] = MathF.Sqrt(x) + MathF.Sin(x) * MathF.Cos(x)
                  + MathF.Log(x + 1) + MathF.Exp(x * 0.001f)
                  + MathF.Atan(x * 0.01f);
    }
}

public struct DemoMathJob : IJobParallelFor
{
    public int[] Data;
    public int Seed;
    public void Execute(int i)
    {
        int v = Data[i];
        Data[i] = (v * 7 + Seed) % 10007;
        float f = MathF.Sqrt(v * 0.001f) + MathF.Sin(v * 0.01f);
        if (f > 1000) Data[i] = (int)f;
    }
}

public struct DemoLightJob2 : IJobParallelFor
{
    public int[] Data;
    public int Factor;
    public void Execute(int i)
    {
        Data[i] = (Data[i] * Factor) % 10007;
    }
}

public struct DemoIJob1 : IJob
{
    public int Iterations;
    public void Execute()
    {
        double acc = 0;
        for (int i = 0; i < Iterations; i++)
            acc += MathF.Sin(i * 0.001f) + MathF.Cos(i * 0.002f);
    }
}

public struct DemoIJob2 : IJob
{
    public int Iterations;
    public void Execute()
    {
        double acc = 0;
        for (int i = 0; i < Iterations; i++)
            acc += MathF.Exp(MathF.Sin(i * 0.001f)) + MathF.Log(i * 0.0001f + 1);
    }
}
