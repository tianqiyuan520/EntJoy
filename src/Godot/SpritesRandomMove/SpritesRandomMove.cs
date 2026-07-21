using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;
using Godot;
using System;
using System.Diagnostics;

public struct Position : IComponentData
{
	public float2 pos;
}

public struct Vel : IComponentData
{
	public float2 vel;
}

public partial class SpritesRandomMove : Node2D
{
	[Export]
	Node MultiMeshgroup;

	public World myWorld;

	public int EntityCount = 1_000_000;
	public bool isPaused = false;

	private Rect2 viewportRect;

	private readonly Stopwatch _sw = new Stopwatch();
	private double _totalMs = 0;
	private int _frameCount = 0;

	private const int EcsFrameBenchmarkWarmupFrames = 20;
	private const int EcsFrameBenchmarkMeasureFrames = 100;
	private bool _ecsFrameBenchmarkActive = false;
	private bool _ecsAutoBenchmarkActive = false;
	private int _ecsAutoBenchmarkIndex = 0;
	private int _ecsFrameBenchmarkJobType = 0;
	private int _ecsFrameBenchmarkFrame = 0;
	private readonly double[] _ecsFrameBenchmarkTotals = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkSchedules = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkCompletes = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkAssistChunks = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkWorkerRanges = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkMainRanges = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkParkWakes = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkDeferredRuns = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkPublishedJobs = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkPrewakes = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkHotSpinHits = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkWaitFallbacks = new double[EcsFrameBenchmarkMeasureFrames];
	private readonly double[] _ecsFrameBenchmarkNotifiedWorkers = new double[EcsFrameBenchmarkMeasureFrames];

	private bool _useECS = true;
	private Label _modeLabel;

	private QueryBuilder _moveQuery = new QueryBuilder().WithAll<Position, Vel>();
	private int _ecsJobType = 0;
	private readonly string[] _ecsJobTypeNames =
	{
		"ECS C# IJobChunk",
		"ECS C++ IJobChunk",
		"ECS ISPC IJobChunk",
		"ECS C# IJobEntity",
		"ECS C++ IJobEntity",
		"ECS ISPC IJobEntity"
	};
	private readonly int[] _ecsAutoBenchmarkJobTypes = { 0, 1, 2, 3, 4, 5 };

	private NativeArray<float2> _naPositions;
	private NativeArray<float2> _naVelocities;
	private bool _naInitialized = false;

	private NativeMoveJob _naMoveJob;
	private NativeMoveJob_NativeCpp _naMoveJobCpp;
	private NativeMoveJob_NativeIspc _naMoveJobIspc;

	private int _naJobType = 0;
	private readonly string[] _naJobTypeNames = { "JobSystem(C#)", "Native C++", "Native ISPC" };

	public override void _Ready()
	{
		GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateWorld").Pressed += CreateWorld;
		GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateEntity").Pressed += NewEntity;
		GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("PrintEntity").Pressed += Display;
		GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Report").Pressed += Report;
		GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Pause").Pressed += Pause;

		var benchBtn = GetNode<Button>("CanvasLayer/HBoxContainer/Benchmark");
		if (benchBtn == null)
		{
			benchBtn = new Button { Text = "Benchmark" };
			GetNode("CanvasLayer/HBoxContainer").AddChild(benchBtn);
		}
		benchBtn.Pressed += RunBenchmark;

		var toggleBtn = GetNode<Button>("CanvasLayer/HBoxContainer/ToggleMode");
		if (toggleBtn == null)
		{
			toggleBtn = new Button { Text = "切换模式" };
			GetNode("CanvasLayer/HBoxContainer").AddChild(toggleBtn);
		}
		toggleBtn.Pressed += ToggleMode;

		var toggleJobBtn = GetNode<Button>("CanvasLayer/HBoxContainer/ToggleJobType");
		if (toggleJobBtn == null)
		{
			toggleJobBtn = new Button { Text = "切换Job类型" };
			GetNode("CanvasLayer/HBoxContainer").AddChild(toggleJobBtn);
		}
		toggleJobBtn.Pressed += ToggleNaJobType;

		_modeLabel = GetNode<Label>("CanvasLayer/HBoxContainer/ModeLabel");
		if (_modeLabel == null)
		{
			_modeLabel = new Label { Text = "[ECS]" };
			GetNode("CanvasLayer/HBoxContainer").AddChild(_modeLabel);
		}

		viewportRect = GetViewportRect();
		UpdateModeLabel();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (isPaused)
			return;

		double elapsedMs;

		if (_useECS)
		{
			if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
				return;

			NativeJobSystemStats beforeStats = default;
			if (_ecsFrameBenchmarkActive)
			{
				beforeStats = NativeJobScheduler.GetStats();
			}

			int jobType = _ecsFrameBenchmarkActive ? _ecsFrameBenchmarkJobType : _ecsJobType;
			ScheduleAndCompleteEcsMoveJob((float)delta, jobType, out double scheduleMs, out double completeMs);
			elapsedMs = scheduleMs + completeMs;

			if (_ecsFrameBenchmarkActive)
			{
				NativeJobSystemStats afterStats = NativeJobScheduler.GetStats();
				RecordEcsFrameBenchmarkSample(
					scheduleMs,
					completeMs,
					Diff(afterStats.AssistExecuted, beforeStats.AssistExecuted),
					Diff(afterStats.WorkerExecutedRanges, beforeStats.WorkerExecutedRanges),
					Diff(afterStats.MainExecutedRanges, beforeStats.MainExecutedRanges),
					Diff(afterStats.ParkWakeCount, beforeStats.ParkWakeCount),
					Diff(afterStats.DeferredRuns, beforeStats.DeferredRuns),
					Diff(afterStats.PublishedJobs, beforeStats.PublishedJobs),
					Diff(afterStats.PrewakeCount, beforeStats.PrewakeCount),
					Diff(afterStats.HotSpinHits, beforeStats.HotSpinHits),
					Diff(afterStats.WaitFallbacks, beforeStats.WaitFallbacks),
					Diff(afterStats.NotifiedWorkers, beforeStats.NotifiedWorkers));
			}
		}
		else
		{
			if (!_naInitialized || _naPositions.Length == 0)
				return;

			_sw.Restart();
			switch (_naJobType)
			{
				case 0:
					_naMoveJob.Dt = (float)delta;
					_naMoveJob.ViewportWidth = (float)viewportRect.Size.X;
					_naMoveJob.ViewportHeight = (float)viewportRect.Size.Y;
					_naMoveJob.Schedule(EntityCount, 0).Complete();
					break;
				case 1:
					_naMoveJobCpp.Dt = (float)delta;
					_naMoveJobCpp.ViewportWidth = (float)viewportRect.Size.X;
					_naMoveJobCpp.ViewportHeight = (float)viewportRect.Size.Y;
					_naMoveJobCpp.Schedule(EntityCount, 0).Complete();
					break;
				case 2:
					_naMoveJobIspc.Dt = (float)delta;
					_naMoveJobIspc.ViewportWidth = (float)viewportRect.Size.X;
					_naMoveJobIspc.ViewportHeight = (float)viewportRect.Size.Y;
					_naMoveJobIspc.Schedule(EntityCount, 0).Complete();
					break;
			}
			_sw.Stop();
			elapsedMs = _sw.Elapsed.TotalMilliseconds;
		}

		if (!_ecsFrameBenchmarkActive)
		{
			_totalMs += elapsedMs;
			_frameCount++;

			if (_frameCount >= 60)
			{
				double avg = _totalMs / _frameCount;
				GD.Print($"[{GetCurrentJobTypeName()}] 每帧平均耗时(60帧): {avg:F4} ms");
				_totalMs = 0;
				_frameCount = 0;
			}
		}
	}

	private static ulong Diff(ulong after, ulong before) => after >= before ? after - before : 0;

	private void ScheduleEcsMoveJob(float dt)
	{
		ScheduleAndCompleteEcsMoveJob(dt, _ecsJobType, out _, out _);
	}

	private void ScheduleAndCompleteEcsMoveJob(float dt, int jobType, out double scheduleMs, out double completeMs)
	{
		JobHandle handle = default;

		_sw.Restart();
		switch (jobType)
		{
			case 0:
				handle = new MoveSystemJobCSharp { dt = dt }.Schedule(_moveQuery);
				break;
			case 1:
				handle = new MoveSystemJobCpp { dt = dt }.ScheduleWithWorkerCapAndRangeSize(_moveQuery, 0, 64);
				break;
			case 2:
				handle = new MoveSystemJobIspc { dt = dt }.Schedule(_moveQuery);
				break;
			case 3:
				handle = new MoveSystemJobEntityCSharp { dt = dt }.Schedule(_moveQuery);
				break;
			case 4:
				handle = new MoveSystemJobEntityCpp { dt = dt }.Schedule(_moveQuery);
				break;
			case 5:
				handle = new MoveSystemJobEntityIspc { dt = dt }.Schedule(_moveQuery);
				break;
		}
		_sw.Stop();
		scheduleMs = _sw.Elapsed.TotalMilliseconds;

		_sw.Restart();
		if (jobType >= 0 && jobType <= 5)
		{
			handle.Complete();
		}
		_sw.Stop();
		completeMs = _sw.Elapsed.TotalMilliseconds;
	}

	private string GetCurrentJobTypeName()
	{
		return _useECS ? _ecsJobTypeNames[_ecsJobType] : _naJobTypeNames[_naJobType];
	}

	private void StartEcsFrameBenchmark(int? jobTypeOverride = null)
	{
		if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
		{
			GD.Print("请先创建世界和实体");
			return;
		}

		_ecsFrameBenchmarkActive = true;
		_ecsFrameBenchmarkJobType = jobTypeOverride ?? _ecsJobType;
		_ecsFrameBenchmarkFrame = 0;
		_totalMs = 0;
		_frameCount = 0;

		string jobName = _ecsJobTypeNames[_ecsFrameBenchmarkJobType];
		if (_ecsAutoBenchmarkActive)
			GD.Print($"\n[{_ecsAutoBenchmarkIndex + 1}/{_ecsAutoBenchmarkJobTypes.Length}] {jobName}");
		GD.Print($"[{jobName}] Frame-spaced benchmark started.");
		GD.Print($"[{jobName}] one Schedule+Complete per _PhysicsProcess frame");
		GD.Print($"[{jobName}] Warmup={EcsFrameBenchmarkWarmupFrames}, Measure={EcsFrameBenchmarkMeasureFrames}");
	}

	private void CancelEcsFrameBenchmark(string reason)
	{
		if (!_ecsFrameBenchmarkActive)
			return;

		string jobName = _ecsJobTypeNames[_ecsFrameBenchmarkJobType];
		_ecsFrameBenchmarkActive = false;
		_ecsAutoBenchmarkActive = false;
		_ecsAutoBenchmarkIndex = 0;
		_ecsFrameBenchmarkFrame = 0;
		_totalMs = 0;
		_frameCount = 0;
		GD.Print($"[{jobName}] Frame-spaced benchmark cancelled: {reason}");
	}

	private void RecordEcsFrameBenchmarkSample(double scheduleMs, double completeMs, ulong assistChunks, ulong workerRanges, ulong mainRanges, ulong parkWakes, ulong deferredRuns, ulong publishedJobs, ulong prewakes, ulong hotSpinHits, ulong waitFallbacks, ulong notifiedWorkers)
	{
		int frame = _ecsFrameBenchmarkFrame++;
		if (frame < EcsFrameBenchmarkWarmupFrames)
			return;

		int measureIndex = frame - EcsFrameBenchmarkWarmupFrames;
		if (measureIndex == 0)
		{
			GD.Print($"[{_ecsJobTypeNames[_ecsFrameBenchmarkJobType]}] Warmup finished; measuring frame-spaced ECS job...");
		}

		if (measureIndex >= EcsFrameBenchmarkMeasureFrames)
			return;

		_ecsFrameBenchmarkSchedules[measureIndex] = scheduleMs;
		_ecsFrameBenchmarkCompletes[measureIndex] = completeMs;
		_ecsFrameBenchmarkTotals[measureIndex] = scheduleMs + completeMs;
		_ecsFrameBenchmarkAssistChunks[measureIndex] = assistChunks;
		_ecsFrameBenchmarkWorkerRanges[measureIndex] = workerRanges;
		_ecsFrameBenchmarkMainRanges[measureIndex] = mainRanges;
		_ecsFrameBenchmarkParkWakes[measureIndex] = parkWakes;
		_ecsFrameBenchmarkDeferredRuns[measureIndex] = deferredRuns;
		_ecsFrameBenchmarkPublishedJobs[measureIndex] = publishedJobs;
		_ecsFrameBenchmarkPrewakes[measureIndex] = prewakes;
		_ecsFrameBenchmarkHotSpinHits[measureIndex] = hotSpinHits;
		_ecsFrameBenchmarkWaitFallbacks[measureIndex] = waitFallbacks;
		_ecsFrameBenchmarkNotifiedWorkers[measureIndex] = notifiedWorkers;

		if (measureIndex + 1 >= EcsFrameBenchmarkMeasureFrames)
		{
			FinishEcsFrameBenchmark();
		}
	}

	private void FinishEcsFrameBenchmark()
	{
		string jobName = _ecsJobTypeNames[_ecsFrameBenchmarkJobType];
		double avgTotal = Average(_ecsFrameBenchmarkTotals, EcsFrameBenchmarkMeasureFrames);
		double avgSchedule = Average(_ecsFrameBenchmarkSchedules, EcsFrameBenchmarkMeasureFrames);
		double avgComplete = Average(_ecsFrameBenchmarkCompletes, EcsFrameBenchmarkMeasureFrames);
		double avgAssistChunks = Average(_ecsFrameBenchmarkAssistChunks, EcsFrameBenchmarkMeasureFrames);
		double avgWorkerRanges = Average(_ecsFrameBenchmarkWorkerRanges, EcsFrameBenchmarkMeasureFrames);
		double avgMainRanges = Average(_ecsFrameBenchmarkMainRanges, EcsFrameBenchmarkMeasureFrames);
		double avgParkWakes = Average(_ecsFrameBenchmarkParkWakes, EcsFrameBenchmarkMeasureFrames);
		double avgDeferredRuns = Average(_ecsFrameBenchmarkDeferredRuns, EcsFrameBenchmarkMeasureFrames);
		double avgPublishedJobs = Average(_ecsFrameBenchmarkPublishedJobs, EcsFrameBenchmarkMeasureFrames);
		double avgPrewakes = Average(_ecsFrameBenchmarkPrewakes, EcsFrameBenchmarkMeasureFrames);
		double avgHotSpinHits = Average(_ecsFrameBenchmarkHotSpinHits, EcsFrameBenchmarkMeasureFrames);
		double avgWaitFallbacks = Average(_ecsFrameBenchmarkWaitFallbacks, EcsFrameBenchmarkMeasureFrames);
		double avgNotifiedWorkers = Average(_ecsFrameBenchmarkNotifiedWorkers, EcsFrameBenchmarkMeasureFrames);
		double minTotal = Min(_ecsFrameBenchmarkTotals, EcsFrameBenchmarkMeasureFrames);
		double maxTotal = Max(_ecsFrameBenchmarkTotals, EcsFrameBenchmarkMeasureFrames);
		double p95Total = Percentile(_ecsFrameBenchmarkTotals, EcsFrameBenchmarkMeasureFrames, 0.95);

		_ecsFrameBenchmarkActive = false;
		_ecsFrameBenchmarkFrame = 0;

		GD.Print($"Frame-spaced {jobName}: avgTotal={avgTotal:F4} ms avgSchedule={avgSchedule:F4} ms avgComplete={avgComplete:F4} ms avgAssistChunks={avgAssistChunks:F1} min={minTotal:F4} ms max={maxTotal:F4} ms p95={p95Total:F4} ms");
		GD.Print($"Frame-spaced {jobName}: avgWorkerRanges={avgWorkerRanges:F1} avgMainRanges={avgMainRanges:F1} avgParkWakes={avgParkWakes:F1} avgDeferredRuns={avgDeferredRuns:F1} avgPublishedJobs={avgPublishedJobs:F1}");
		GD.Print($"Frame-spaced {jobName}: avgPrewakes={avgPrewakes:F1} avgHotSpinHits={avgHotSpinHits:F1} avgWaitFallbacks={avgWaitFallbacks:F1} avgNotifiedWorkers={avgNotifiedWorkers:F1}");
		GD.Print($"Frame-spaced {jobName}: one Schedule+Complete per _PhysicsProcess frame, samples={EcsFrameBenchmarkMeasureFrames}");

		if (_ecsAutoBenchmarkActive)
		{
			_ecsAutoBenchmarkIndex++;
			if (_ecsAutoBenchmarkIndex < _ecsAutoBenchmarkJobTypes.Length)
			{
				StartEcsFrameBenchmark(_ecsAutoBenchmarkJobTypes[_ecsAutoBenchmarkIndex]);
			}
			else
			{
				_ecsAutoBenchmarkActive = false;
				_ecsAutoBenchmarkIndex = 0;
				GD.Print("\n=== ECS IJobChunk 自动基准测试完成 ===");
			}
		}
	}

	private static double Average(double[] values, int count)
	{
		double total = 0;
		for (int i = 0; i < count; i++) total += values[i];
		return total / count;
	}

	private static double Min(double[] values, int count)
	{
		double min = double.MaxValue;
		for (int i = 0; i < count; i++) min = Math.Min(min, values[i]);
		return min;
	}

	private static double Max(double[] values, int count)
	{
		double max = double.MinValue;
		for (int i = 0; i < count; i++) max = Math.Max(max, values[i]);
		return max;
	}

	private static double Percentile(double[] values, int count, double percentile)
	{
		double[] sorted = new double[count];
		Array.Copy(values, sorted, count);
		Array.Sort(sorted);
		int index = Math.Clamp((int)Math.Ceiling(percentile * count) - 1, 0, count - 1);
		return sorted[index];
	}

	public void ToggleMode()
	{
		CancelEcsFrameBenchmark("mode switched");
		_useECS = !_useECS;
		UpdateModeLabel();
		GD.Print($"切换至 {(_useECS ? "ECS" : _naJobTypeNames[_naJobType])} 模式");

		if (!_useECS && !_naInitialized && myWorld != null)
		{
			InitNativeArraysFromECS();
		}
	}

	public void ToggleNaJobType()
	{
		if (_useECS)
		{
			CancelEcsFrameBenchmark("job type switched");
			_ecsJobType = (_ecsJobType + 1) % _ecsJobTypeNames.Length;
			UpdateModeLabel();
			GD.Print($"ECS IJobChunk 类型切换至: {_ecsJobTypeNames[_ecsJobType]}");
			return;
		}

		ToggleNativeArrayJobType();
	}

	private void ToggleNativeArrayJobType()
	{
		if (_useECS) return;
		_naJobType = (_naJobType + 1) % 3;
		UpdateModeLabel();
		GD.Print($"NativeArray Job 类型切换至: {_naJobTypeNames[_naJobType]}");

		if (_naJobType == 1 || _naJobType == 2)
		{
			string cwd = System.Environment.CurrentDirectory;
			string asmLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
			string entryLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "null";

			GD.Print("");
			GD.Print("========== Native DLL 调试信息 ==========");
			GD.Print($"CurrentDirectory: {cwd}");
			GD.Print($"Assembly.Location: {asmLocation}");
			GD.Print($"EntryAssembly.Location: {entryLocation}");

			string[] pathsToCheck =
			{
				System.IO.Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Debug", "NativeDll.dll"),
				System.IO.Path.Combine(cwd, "..", "..", "bin", "NativeDll.dll"),
				@"D:\Godot\Project\EntJoy\bin\NativeDll.dll",
				System.IO.Path.Combine(System.IO.Path.GetDirectoryName(asmLocation) ?? "", "NativeDll.dll"),
			};

			foreach (var path in pathsToCheck)
			{
				string full = System.IO.Path.GetFullPath(path);
				GD.Print($"  {(System.IO.File.Exists(full) ? "[OK] " : "[MISS]")} {full}");
			}
			GD.Print("=========================================");
			GD.Print("");
		}
	}

	private void UpdateModeLabel()
	{
		if (_modeLabel != null)
		{
			_modeLabel.Text = $"[{GetCurrentJobTypeName()}]";
		}
	}

	private void InitNativeArraysFromECS()
	{
		if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
		{
			GD.Print("NativeArray: 无实体数据可用");
			return;
		}

		GD.Print("正在从 ECS 复制数据到 NativeArray...");
		var sw = Stopwatch.StartNew();

		if (_naPositions.IsCreated) _naPositions.Dispose();
		if (_naVelocities.IsCreated) _naVelocities.Dispose();

		_naPositions = new NativeArray<float2>(EntityCount, Allocator.Persistent);
		_naVelocities = new NativeArray<float2>(EntityCount, Allocator.Persistent);

		int idx = 0;
		foreach (var chunk in SystemAPI.QueryChunks<Position, Vel>())
		{
			var positions = chunk.GetSpan0();
			var velocities = chunk.GetSpan1();
			int len = chunk.Length;

			for (int i = 0; i < len; i++)
			{
				if (idx >= _naPositions.Length) break; // 防止 EntityCount < 实际数量时越界写
				float2 p = positions[i].pos;
				float2 v = velocities[i].vel;
				_naPositions[idx] = new float2(p.x, p.y);
				_naVelocities[idx] = new float2(v.x, v.y);
				idx++;
			}
		}

		float vw = (float)viewportRect.Size.X;
		float vh = (float)viewportRect.Size.Y;

		_naMoveJob = new NativeMoveJob { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };
		_naMoveJobCpp = new NativeMoveJob_NativeCpp { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };
		_naMoveJobIspc = new NativeMoveJob_NativeIspc { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };

		_naInitialized = true;
		sw.Stop();
		GD.Print($"NativeArray 初始化完成: {EntityCount:N0} 个实体, 耗时 {sw.Elapsed.TotalMilliseconds:F1} ms");
	}

	public void RunBenchmark()
	{
		const int WARMUP = 3;
		const int ITERATIONS = 1000;
		var sw = new Stopwatch();

		if (_useECS)
		{
			if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
			{
				GD.Print("请先创建世界和实体");
				return;
			}

			if (_ecsFrameBenchmarkActive)
			{
				CancelEcsFrameBenchmark("benchmark restarted");
			}

			_ecsAutoBenchmarkActive = true;
			_ecsAutoBenchmarkIndex = 0;
			GD.Print($"\n=== ECS IJobChunk 自动基准测试开始，共 {_ecsAutoBenchmarkJobTypes.Length} 个模式 ===");
			StartEcsFrameBenchmark(_ecsAutoBenchmarkJobTypes[_ecsAutoBenchmarkIndex]);
			return;
		}

		if (!_naInitialized || _naPositions.Length == 0)
		{
			GD.Print("请先创建实体");
			return;
		}

		float vw = (float)viewportRect.Size.X;
		float vh = (float)viewportRect.Size.Y;

		GD.Print("\n=== NativeArray Benchmark ===");

		GD.Print($"\n[JobSystem(C#)] 预热 {WARMUP} 次...");
		for (int i = 0; i < WARMUP; i++)
		{
			new NativeMoveJob { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh }.Schedule(EntityCount, 65536).Complete();
		}

		GD.Print($"[JobSystem(C#)] 开始基准测试 {ITERATIONS} 次...");
		double total1 = 0;
		for (int i = 0; i < ITERATIONS; i++)
		{
			var job = new NativeMoveJob { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };
			sw.Restart();
			job.Schedule(EntityCount, 65536).Complete();
			sw.Stop();
			total1 += sw.Elapsed.TotalMilliseconds;
		}
		double avg1 = total1 / ITERATIONS;

		GD.Print($"\n[Native C++] 预热 {WARMUP} 次...");
		for (int i = 0; i < WARMUP; i++)
		{
			new NativeMoveJob_NativeCpp { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh }.Schedule(EntityCount, 65536).Complete();
		}

		GD.Print($"[Native C++] 开始基准测试 {ITERATIONS} 次...");
		double total2 = 0;
		for (int i = 0; i < ITERATIONS; i++)
		{
			var job = new NativeMoveJob_NativeCpp { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };
			sw.Restart();
			job.Schedule(EntityCount, 65536).Complete();
			sw.Stop();
			total2 += sw.Elapsed.TotalMilliseconds;
		}
		double avg2 = total2 / ITERATIONS;

		GD.Print($"\n[Native ISPC] 预热 {WARMUP} 次...");
		for (int i = 0; i < WARMUP; i++)
		{
			new NativeMoveJob_NativeIspc { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh }.Schedule(EntityCount, 65536).Complete();
		}

		GD.Print($"[Native ISPC] 开始基准测试 {ITERATIONS} 次...");
		double total3 = 0;
		for (int i = 0; i < ITERATIONS; i++)
		{
			var job = new NativeMoveJob_NativeIspc { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };
			sw.Restart();
			job.Schedule(EntityCount, 65536).Complete();
			sw.Stop();
			total3 += sw.Elapsed.TotalMilliseconds;
		}
		double avg3 = total3 / ITERATIONS;

		GD.Print("\n=== NativeArray Benchmark 结果 ===");
		GD.Print($"JobSystem(C#):     {avg1,8:F3} ms");
		GD.Print($"Native C++:        {avg2,8:F3} ms (加速比 {avg1 / avg2:F2}x)");
		GD.Print($"Native ISPC:       {avg3,8:F3} ms (加速比 {avg1 / avg3:F2}x)");
	}

	public void CreateWorld()
	{
		myWorld = new World();
		GD.Print("创建世界成功;");
	}

	public void NewEntity()
	{
		if (_useECS)
		{
			for (int i = 0; i < EntityCount; i++)
			{
				var entity = myWorld.EntityManager.NewEntity(typeof(Position), typeof(Vel));
				myWorld.EntityManager.AddComponent(entity, new Position { pos = new float2(100f, 100f) });
				myWorld.EntityManager.AddComponent(entity, new Vel
				{
					vel = new float2((float)GD.RandRange(100.0, 200.0), (float)GD.RandRange(-200.0, 200.0))
				});
			}

			int realCount = myWorld.EntityManager.EntityCount;
			GD.Print($"NewEntity Success 当前实体数 {realCount}");
		}
		else
		{
			if (_naPositions.IsCreated) _naPositions.Dispose();
			if (_naVelocities.IsCreated) _naVelocities.Dispose();

			_naPositions = new NativeArray<float2>(EntityCount, Allocator.Persistent);
			_naVelocities = new NativeArray<float2>(EntityCount, Allocator.Persistent);

			for (int i = 0; i < EntityCount; i++)
			{
				_naPositions[i] = new float2(100f, 100f);
				_naVelocities[i] = new float2((float)GD.RandRange(100.0, 200.0), (float)GD.RandRange(-200.0, 200.0));
			}

			float vw = (float)viewportRect.Size.X;
			float vh = (float)viewportRect.Size.Y;

			_naMoveJob = new NativeMoveJob { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };
			_naMoveJobCpp = new NativeMoveJob_NativeCpp { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };
			_naMoveJobIspc = new NativeMoveJob_NativeIspc { Positions = _naPositions, Velocities = _naVelocities, Dt = 0.016f, ViewportWidth = vw, ViewportHeight = vh };

			_naInitialized = true;
			GD.Print($"NativeArray NewEntity Success 当前实体数 {EntityCount}");
		}
	}

	public void Display()
	{
		if (_useECS)
		{
			int index = 0;
			foreach (var chunk in SystemAPI.QueryChunks<Position, Vel>())
			{
				var positions = chunk.GetSpan0();
				int length = chunk.Length;
				for (int i = 0; i < length && index < 30; i++)
				{
					index++;
					GD.Print(index, " ", positions[i].pos);
				}
			}
		}
		else
		{
			int count = Mathf.Min(30, (int)_naPositions.Length);
			for (int i = 0; i < count; i++)
			{
				float2 p = _naPositions[i];
				GD.Print(i + 1, " (", p.x, ", ", p.y, ")");
			}
		}
	}

	public void Report()
	{
	}

	public void Pause()
	{
		isPaused = !isPaused;
		GD.Print($"暂停状态: {isPaused}");
	}

	public override void _ExitTree()
	{
		base._ExitTree();
		if (_naPositions.IsCreated) _naPositions.Dispose();
		if (_naVelocities.IsCreated) _naVelocities.Dispose();
	}
}

/// <summary>
/// ECS C# IJobChunk 位移 Job。
/// </summary>
public unsafe struct MoveSystemJobCSharp : IJobChunk
{
	public float dt;

	public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
	{
		var positions = chunk.GetComponentDataSpan<Position>();
		var velocities = chunk.GetComponentDataSpan<Vel>();

		for (int i = 0; i < positions.Length; i++)
		{
			Position position = positions[i];
			Vel velocity = velocities[i];
			position.pos.x += velocity.vel.x * dt;
			position.pos.y += velocity.vel.y * dt;
			positions[i] = position;
		}
	}
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public unsafe struct MoveSystemJobCpp : IJobChunk
{
	public float dt;

	public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
	{
		var positions = chunk.GetComponentDataNativeArray<Position>();
		var velocities = chunk.GetComponentDataNativeArray<Vel>();

		for (int i = 0; i < positions.Length; i++)
		{
			Position position = positions[i];
			Vel velocity = velocities[i];
			position.pos.x += velocity.vel.x * dt;
			position.pos.y += velocity.vel.y * dt;
			positions[i] = position;
		}
	}
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
public unsafe struct MoveSystemJobIspc : IJobChunk
{
	public float dt;

	public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
	{
		var positions = chunk.GetComponentDataNativeArray<Position>();
		var velocities = chunk.GetComponentDataNativeArray<Vel>();

		for (int i = 0; i < positions.Length; i++)
		{
			Position position = positions[i];
			Vel velocity = velocities[i];
			position.pos.x += velocity.vel.x * dt;
			position.pos.y += velocity.vel.y * dt;
			positions[i] = position;
		}
	}
}

public struct MoveSystemJobEntityCSharp : IJobEntity
{
	public float dt;

	public void Execute(ref Position position, in Vel velocity)
	{
		position.pos.x += velocity.vel.x * dt;
		position.pos.y += velocity.vel.y * dt;
	}
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct MoveSystemJobEntityCpp : IJobEntity
{
	public float dt;

	public void Execute(ref Position position, in Vel velocity)
	{
		position.pos.x += velocity.vel.x * dt;
		position.pos.y += velocity.vel.y * dt;
	}
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
public struct MoveSystemJobEntityIspc : IJobEntity
{
	public float dt;

	public void Execute(ref Position position, in Vel velocity)
	{
		position.pos.x += velocity.vel.x * dt;
		position.pos.y += velocity.vel.y * dt;
	}
}

public struct NativeMoveJob : IJobParallelFor
{
	public NativeArray<float2> Positions;
	public NativeArray<float2> Velocities;
	public float Dt;
	public float ViewportWidth;
	public float ViewportHeight;

	public void Execute(int index)
	{
		float2 pos = Positions[index];
		float2 vel = Velocities[index];

		pos.x += vel.x * Dt;
		pos.y += vel.y * Dt;

		if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
		if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

		Positions[index] = pos;
		Velocities[index] = vel;
	}
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct NativeMoveJob_NativeCpp : IJobParallelFor
{
	public NativeArray<float2> Positions;
	public NativeArray<float2> Velocities;
	public float Dt;
	public float ViewportWidth;
	public float ViewportHeight;

	public void Execute(int index)
	{
		float2 pos = Positions[index];
		float2 vel = Velocities[index];

		pos.x += vel.x * Dt;
		pos.y += vel.y * Dt;

		if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
		if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

		Positions[index] = pos;
		Velocities[index] = vel;
	}
}

[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
public struct NativeMoveJob_NativeIspc : IJobParallelFor
{
	public NativeArray<float2> Positions;
	public NativeArray<float2> Velocities;
	public float Dt;
	public float ViewportWidth;
	public float ViewportHeight;

	public void Execute(int index)
	{
		float2 pos = Positions[index];
		float2 vel = Velocities[index];

		pos.x += vel.x * Dt;
		pos.y += vel.y * Dt;

		if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
		if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

		Positions[index] = pos;
		Velocities[index] = vel;
	}
}
