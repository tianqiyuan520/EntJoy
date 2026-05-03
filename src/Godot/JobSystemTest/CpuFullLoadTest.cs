using Godot;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Environment = System.Environment;

public struct HeavyJob : IJobParallelFor
{
    public float[] Input;

    public void Execute(int i)
    {
        float x = Input[i];
        // 复杂运算：sqrt + sin*cos + log
        var res = Math.Sqrt(x) + Math.Sin(x) * Math.Cos(x) + Math.Log(x + 1);
    }
}

public partial class CpuFullLoadTest : Node
{
    private Button _startButton;
    private Label _statusLabel;
    private const int DATA_COUNT = 200_000_000; // 2 亿数据

    public override void _Ready()
    {
        _startButton = GetNode<Button>("Button");
        _statusLabel = GetNode<Label>("Label");
        _startButton.Pressed += OnStartPressed;
    }

    private void OnStartPressed()
    {
        _startButton.Disabled = true;
        _statusLabel.Text = "准备数据...";
        Task.Run(RunTest);
    }

    private void RunTest()
    {
        // 生成测试数据
        float[] data = new float[DATA_COUNT];
        Random rand = new Random(42);
        for (int i = 0; i < DATA_COUNT; i++)
        {
            data[i] = (float)rand.NextDouble() * 100f;
        }

        // ----- 单线程测试 -----
        Stopwatch sw = Stopwatch.StartNew();
        //for (int i = 0; i < DATA_COUNT; i++)
        //{
        //    float x = data[i];
        //    var res = Math.Sqrt(x) + Math.Sin(x) * Math.Cos(x) + Math.Log(x + 1);
        //}
        sw.Stop();
        double singleMs = sw.Elapsed.TotalMilliseconds;

        // ----- 多线程 Job 测试 -----
        var counter = new ThreadCounter(); // 新建独立计数器
        var job = new HeavyJob
        {
            Input = data,
        };

        sw.Restart();
        JobHandle handle = job.Schedule(DATA_COUNT, innerBatchCount: 0, default, counter);
        handle.Complete();
        sw.Stop();
        double parallelMs = sw.Elapsed.TotalMilliseconds;

        int usedThreads = counter.Count; // 从实例获取计数

        CallDeferred(nameof(UpdateUI), singleMs, parallelMs, usedThreads);
    }

    private void UpdateUI(double singleMs, double parallelMs, int usedThreads)
    {
        double speedup = singleMs / parallelMs;
        _statusLabel.Text = $"单线程: {singleMs:F3} ms\n" +
                            $"多线程: {parallelMs:F3} ms\n" +
                            $"加速比: {speedup:F2}x\n" +
                            $"实际线程数: {usedThreads} (逻辑核心: {Environment.ProcessorCount})";
        _startButton.Disabled = false;
    }
}
