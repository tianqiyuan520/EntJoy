using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

public partial class JobProfilerPanel : Panel
{
    private const int MAX = 120;
    private const int MIN_HEIGHT = 100;
    private const int MAX_HEIGHT = 600;
    private const int DEFAULT_HEIGHT = 200;
    private const int DRAG_EDGE = 6;

    private struct Snap
    {
        public int Id;
        public ulong PhysicsTick;
        public ProfilerEntry[] Entries;
        public float CpuPercent;
        public float JobMs;
    }
    private Snap[] _ring = new Snap[MAX];
    private int _cnt, _head, _fid;
    private int _off;

    private Button _btnE, _btnC, _btnP, _btnView;
    private Button _btnPrev, _btnNext;
    private HSlider _sld;
    private Label _lblF;
    private JobTimeline _timeline;
    private RichTextLabel _textView;
    private JobPerfGraph _perfGraph;
    private ScrollContainer _ganttScroll, _textScroll;
    private bool _on, _paused, _userDragging;
    private bool _ganttMode = true;

    private int _panelHeight = DEFAULT_HEIGHT;
    private bool _isDragging;
    private float _dragStartMouseY;
    private int _dragStartHeight;

    private ColorRect _bg;

    private int _lastGeneration = -1;
    private int _skipFramesAfterClear = 0;

    // CPU% 计算
    private TimeSpan _prevCpuTime;
    private long _prevCpuWallTicks;

    public bool IsEnabled => _on;

    public override void _Ready()
    {
        Name = "JobProfilerPanel";
        MouseFilter = MouseFilterEnum.Pass;

        _bg = new ColorRect
        {
            Color = new Color(0.06f, 0.06f, 0.08f, 0.95f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_bg);

        var vb = new VBoxContainer();
        vb.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(vb);

        BuildTopBar(vb);

        _perfGraph = new JobPerfGraph
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 96),
        };
        vb.AddChild(_perfGraph);

        _ganttScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        vb.AddChild(_ganttScroll);

        _timeline = new JobTimeline
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _ganttScroll.AddChild(_timeline);

        _textScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
            Visible = false,
        };
        vb.AddChild(_textScroll);

        _textView = new RichTextLabel
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            BbcodeEnabled = true,
            ScrollActive = false,
            FitContent = true,
        };
        _textView.AddThemeColorOverride("default_color", new Color(0.78f, 0.80f, 0.85f));
        _textScroll.AddChild(_textView);

        MouseDefaultCursorShape = CursorShape.Vsize;

        ResetCpuTimer();
    }

    private void ResetCpuTimer()
    {
        var proc = Process.GetCurrentProcess();
        _prevCpuTime = proc.TotalProcessorTime;
        _prevCpuWallTicks = Stopwatch.GetTimestamp();
    }

    // 逻辑核心数，用于归一化 CPU% 到 0~100%（任务管理器风格）
    private static readonly int _coreCount = System.Environment.ProcessorCount;

    private float ComputeCpuPercent()
    {
        long nowWallTicks = Stopwatch.GetTimestamp();
        double wallSec = (double)(nowWallTicks - _prevCpuWallTicks) / Stopwatch.Frequency;

        var proc = Process.GetCurrentProcess();
        TimeSpan nowCpuTime = proc.TotalProcessorTime;
        double cpuSec = (nowCpuTime - _prevCpuTime).TotalSeconds;

        _prevCpuTime = nowCpuTime;
        _prevCpuWallTicks = nowWallTicks;

        if (wallSec < 0.0005) return 0f;

        // CPU% = (进程CPU时间增量 / 真实时间增量) * 100 / 核心数
        // 归一化到 0%~100%，等价于 Windows 任务管理器显示的"CPU"列
        float raw = (float)(cpuSec / wallSec * 100.0 / _coreCount);
        return Math.Clamp(raw, 0f, 100f);
    }

    private void UpdateLayout()
    {
        var vs = GetViewportRect().Size;
        if (vs.Y < 100) vs = new Vector2(1920, 1080);
        Position = new Vector2(0, vs.Y - _panelHeight - 2);
        Size = new Vector2(vs.X, _panelHeight);
        _bg.Position = Vector2.Zero;
        _bg.Size = Size;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Visible)
        {
            UpdateLayout();
            return;
        }
        UpdateLayout();

        if (!_on || _paused || _userDragging || _isDragging)
            return;

        JobProfiler.PullFrameData();

        if (_skipFramesAfterClear > 0)
        {
            _skipFramesAfterClear--;
            return;
        }

        // CPU%
        float cpuPercent = ComputeCpuPercent();

        // Job 数据采集
        int gen = JobProfiler.Generation;
        bool hasNewJobData = (gen != _lastGeneration);
        _lastGeneration = gen;

        ulong currentTick = Engine.GetPhysicsFrames();

        ProfilerEntry[] copy;
        float jobMs = 0;

        if (hasNewJobData)
        {
            var span = JobProfiler.GetCurrentFrameEntries();
            copy = new ProfilerEntry[span.Length];
            for (int i = 0; i < span.Length; i++)
                copy[i] = span[i];

            foreach (var e in copy)
                jobMs += (float)JobProfiler.ComputeElapsedMs(e);
        }
        else
        {
            copy = Array.Empty<ProfilerEntry>();
        }

        // 记录到性能图
        _perfGraph.RecordFrame(cpuPercent, _fid, currentTick);

        int slot = _head % MAX;
        _ring[slot] = new Snap
        {
            Id = _fid++,
            PhysicsTick = currentTick,
            Entries = copy,
            CpuPercent = cpuPercent,
            JobMs = jobMs,
        };
        _head++;
        _cnt = Math.Min(_cnt + 1, MAX);
        _btnC.Disabled = false;

        // 游标控制：如果图上没有游标，保持最新
        if (_perfGraph.CursorAbsIndex < 0)
        {
            _off = 0;
        }
        _sld.SetValueNoSignal(_cnt - 1 - _off);
        _sld.MaxValue = Math.Max(0, _cnt - 1);

        SyncCursorToFrame();
        RefreshView();
    }

    private void SyncCursorToFrame()
    {
        int ci = _perfGraph.CursorFrameIndex;
        if (ci < 0) return;

        int count = Math.Min(_cnt, MAX);
        if (count == 0) return;
        int oldest = (_head - count + MAX) % MAX;
        int absIdx = (oldest + ci) % MAX;

        for (int i = 0; i < count; i++)
        {
            int idx = (oldest + i) % MAX;
            if (_ring[idx].Id >= 0 && idx == absIdx)
            {
                int targetOff = (_head - 1 - idx + MAX) % MAX;
                if (targetOff < _cnt)
                {
                    _off = targetOff;
                    _sld.SetValueNoSignal(_cnt - 1 - _off);
                    RefreshView();
                }
                break;
            }
        }
    }

    /// <summary>
    /// 当用户通过按钮或滑块切换帧时，同步 CPU 图游标
    /// </summary>
    private void SyncCpuCursorToCurrentFrame()
    {
        if (_cnt <= 0) return;
        int index = (_head - 1 - _off + MAX) % MAX;
        int frameId = _ring[index].Id;
        _perfGraph.SetCursorByFrameId(frameId);
    }

    private void BuildTopBar(VBoxContainer parent)
    {
        var top = new HBoxContainer
        {
            CustomMinimumSize = new Vector2(0, 24),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        parent.AddChild(top);

        _btnE = new Button { Text = "Enable", CustomMinimumSize = new Vector2(60, 20) };
        _btnE.Pressed += () =>
        {
            _on = !_on;
            if (_on)
            {
                JobProfiler.Enabled = true;
                EnabledClear();
                _lastGeneration = -1;
                _btnE.Text = "Disable";
            }
            else
            {
                JobProfiler.Enabled = false;
                OnClear();
                _btnE.Text = "Enable";
            }
        };
        top.AddChild(_btnE);

        _btnP = new Button { Text = "||", CustomMinimumSize = new Vector2(24, 20) };
        _btnP.Pressed += () =>
        {
            _paused = !_paused;
            _btnP.Text = _paused ? "|>" : "||";
        };
        top.AddChild(_btnP);

        _btnC = new Button { Text = "Clear", CustomMinimumSize = new Vector2(60, 20), Disabled = true };
        _btnC.Pressed += OnClear;
        top.AddChild(_btnC);

        _btnPrev = new Button { Text = "<", CustomMinimumSize = new Vector2(22, 20), Disabled = true };
        _btnPrev.Pressed += () =>
        {
            if (_cnt > 0 && _off < _cnt - 1) { _off++; SyncSlider(); RefreshView(); SyncCpuCursorToCurrentFrame(); }
        };
        top.AddChild(_btnPrev);

        _lblF = new Label
        {
            Text = " Frame -- tick --",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _lblF.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.8f));
        top.AddChild(_lblF);

        _btnNext = new Button { Text = ">", CustomMinimumSize = new Vector2(22, 20), Disabled = true };
        _btnNext.Pressed += () =>
        {
            if (_cnt > 0 && _off > 0) { _off--; SyncSlider(); RefreshView(); SyncCpuCursorToCurrentFrame(); }
        };
        top.AddChild(_btnNext);

        _sld = new HSlider
        {
            MinValue = 0,
            MaxValue = 1,
            Value = 1,
            Step = 1,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(60, 0),
        };
        _sld.DragStarted += () => _userDragging = true;
        _sld.ValueChanged += OnSliderValueChanged;
        _sld.DragEnded += (_) => { _userDragging = false; };
        top.AddChild(_sld);

        _btnView = new Button { Text = "Gantt", CustomMinimumSize = new Vector2(48, 20), ToggleMode = true, ButtonPressed = true };
        _btnView.Pressed += () =>
        {
            _ganttMode = !_ganttMode;
            _btnView.Text = _ganttMode ? "Gantt" : "Text";
            _ganttScroll.Visible = _ganttMode;
            _textScroll.Visible = !_ganttMode;
            RefreshView();
        };
        top.AddChild(_btnView);
    }

    private void SyncSlider()
    {
        if (_cnt > 0) _sld.SetValueNoSignal(_cnt - 1 - _off);
    }

    private void OnSliderValueChanged(double value)
    {
        if (_cnt <= 0) return;
        int v = (int)Math.Round(value);
        v = Math.Clamp(v, 0, _cnt - 1);
        _off = _cnt - 1 - v;
        RefreshView();
        SyncCpuCursorToCurrentFrame();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            float localY = mb.Position.Y;
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && localY <= DRAG_EDGE)
                {
                    _isDragging = true;
                    _dragStartMouseY = mb.GlobalPosition.Y;
                    _dragStartHeight = _panelHeight;
                    MouseFilter = MouseFilterEnum.Stop;
                    AcceptEvent();
                }
                else if (!mb.Pressed && _isDragging)
                {
                    _isDragging = false;
                    MouseFilter = MouseFilterEnum.Pass;
                    AcceptEvent();
                }
            }
        }

        if (@event is InputEventMouseMotion mm && _isDragging)
        {
            float deltaY = mm.GlobalPosition.Y - _dragStartMouseY;
            int newHeight = _dragStartHeight - (int)deltaY;
            newHeight = Math.Clamp(newHeight, MIN_HEIGHT, MAX_HEIGHT);
            if (newHeight != _panelHeight)
            {
                _panelHeight = newHeight;
                UpdateLayout();
            }
            AcceptEvent();
        }
    }

    private void RefreshView()
    {
        if (_cnt == 0)
        {
            _lblF.Text = " Frame -- tick --";
            _btnPrev.Disabled = true;
            _btnNext.Disabled = true;
            _timeline.SetData(ReadOnlySpan<ProfilerEntry>.Empty, true);
            return;
        }

        int index = (_head - 1 - _off + MAX) % MAX;
        var d = _ring[index];

        _lblF.Text = $" Frame {d.Id}  (tick {d.PhysicsTick})  CPU {d.CpuPercent:F1}%  Job {d.JobMs:F2}ms";
        _btnPrev.Disabled = (_off >= _cnt - 1);
        _btnNext.Disabled = (_off <= 0);

        _timeline.SetData(new ReadOnlySpan<ProfilerEntry>(d.Entries), true);

        if (!_ganttMode && _textView != null)
            UpdateTextView(d.Entries);
    }

    private void UpdateTextView(ProfilerEntry[] entries)
    {
        if (entries == null || entries.Length == 0)
        {
            _textView.Text = "[i]No job data[/i]";
            return;
        }

        var sb = new StringBuilder();
        double freq = Stopwatch.Frequency;
        double ticksToMs = 1000.0 / freq;

        var threadGroups = entries.GroupBy(e => e.ThreadIndex).OrderBy(g => g.Key);

        foreach (var group in threadGroups)
        {
            int thread = group.Key;
            double totalMs = 0;
            var jobAgg = new Dictionary<ulong, (string Name, double TotalMs, int Count)>();

            foreach (var e in group)
            {
                double ms = (double)(e.EndCycles - e.StartCycles) * ticksToMs;
                totalMs += ms;
                string name = JobProfiler.GetJobName(e.JobNameHash);

                if (jobAgg.TryGetValue(e.JobNameHash, out var existing))
                    jobAgg[e.JobNameHash] = (name, existing.TotalMs + ms, existing.Count + 1);
                else
                    jobAgg[e.JobNameHash] = (name, ms, 1);
            }

            sb.Append($"[b]Worker {thread}[/b]  [color=#88ff88]{totalMs:F2}ms[/color]\n");
            foreach (var kv in jobAgg.Values.OrderByDescending(j => j.TotalMs))
            {
                sb.Append($"  {kv.Name,-18}  [color=#88ccff]{kv.TotalMs:F3}ms[/color]  ({kv.Count}x)\n");
            }
            sb.Append("\n");
        }

        _textView.Text = sb.ToString();
    }

    private void EnabledClear()
    {
        JobProfiler.Clear();
        _perfGraph.Clear();

        for (int i = 0; i < MAX; i++)
            _ring[i] = default;
        _cnt = _head = _off = _fid = 0;
        _lastGeneration = -1;
        _skipFramesAfterClear = 3;

        ResetCpuTimer();

        _sld.Value = 0;
        _sld.MaxValue = 1;
        _lblF.Text = " Frame -- tick --";
        _btnPrev.Disabled = true;
        _btnNext.Disabled = true;
        _btnC.Disabled = true;
        _paused = false;
        _btnP.Text = "||";
        _userDragging = false;
        _timeline.SetData(ReadOnlySpan<ProfilerEntry>.Empty, true);
    }

    private void OnClear()
    {
        JobProfiler.Clear();
        _perfGraph.Clear();

        for (int i = 0; i < MAX; i++)
            _ring[i] = default;
        _cnt = _head = _off = _fid = 0;
        _lastGeneration = -1;
        _skipFramesAfterClear = 3;

        ResetCpuTimer();

        _sld.Value = 0;
        _sld.MaxValue = 1;
        _lblF.Text = " Frame -- tick --";
        _btnPrev.Disabled = true;
        _btnNext.Disabled = true;
        _btnC.Disabled = true;
        _paused = false;
        _btnP.Text = "||";
        _userDragging = false;
        _timeline.SetData(ReadOnlySpan<ProfilerEntry>.Empty, true);
    }
}
