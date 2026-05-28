using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Unity 风格 Timeline (甘特图) 控件。
/// 支持水平滚动、鼠标滚轮缩放。
/// 条上文字居中：名称在上行，耗时在下行。
/// </summary>
public partial class JobTimeline : Control
{
    private struct Bar
    {
        public string Name;
        public ulong Hash;
        public float OffsetMs;
        public float DurationMs;
        public int WorkerIdx;
        public int JobType;
    }

    private const float LeftMargin = 84f;
    private const float RowHeight = 24f;
    private const float HeaderHeight = 30f;
    // 调高 MinTickPx 使刻度更密，显示 1ms 等小间隔
    private const float MinTickPx = 25f;

    private float _pixelsPerMs = 75f;
    private float _targetPxPerMs = 75f;
    private const float MinBarPxForText = 120f;

    private const float MinJobDurationMs = 0.01f;

    private Bar[] _bars = Array.Empty<Bar>();
    private float _frameDurationMs;
    private int _workerCount;
    private string[] _workerLabels = Array.Empty<string>();

    private int _hoveredBar = -1;

    // 中键拖拽
    private bool _middleDragging;
    private Vector2 _middleDragStart;
    private int _middleScrollStart;

    private Font _font;

    private static readonly Color[] _palette = new[]
    {
        new Color(0.31f, 0.83f, 0.67f),
        new Color(0.95f, 0.61f, 0.27f),
        new Color(0.54f, 0.64f, 0.91f),
        new Color(0.89f, 0.47f, 0.47f),
        new Color(0.71f, 0.54f, 0.87f),
        new Color(0.63f, 0.83f, 0.47f),
        new Color(0.95f, 0.76f, 0.32f),
        new Color(0.48f, 0.80f, 0.82f),
        new Color(0.87f, 0.58f, 0.43f),
        new Color(0.75f, 0.56f, 0.71f),
    };

    private static readonly Color _bgColor = new Color(0.10f, 0.10f, 0.12f, 1f);
    private static readonly Color _gridColor = new Color(0.20f, 0.20f, 0.22f, 1f);
    private static readonly Color _textColor = new Color(0.78f, 0.80f, 0.85f, 1f);
    private static readonly Color _headerBg = new Color(0.14f, 0.14f, 0.16f, 1f);
    private static readonly Color _timeAxisColor = new Color(0.50f, 0.55f, 0.60f, 1f);
    private static readonly Color _workerEven = new Color(0.12f, 0.12f, 0.14f, 1f);
    private static readonly Color _workerOdd = new Color(0.145f, 0.145f, 0.165f, 1f);

    private const float FontSize = 11f;
    private const float TinyFontSize = 8f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Pass;
        _font = ThemeDB.FallbackFont;
        if (_font == null) _font = new FontFile();
        MouseEntered += QueueRedraw;
        MouseExited += () => { _hoveredBar = -1; QueueRedraw(); };
    }

    public void SetData(ReadOnlySpan<ProfilerEntry> entries, bool isCs)
    {
        if (entries.Length == 0)
        {
            _bars = Array.Empty<Bar>();
            _frameDurationMs = 0;
            _workerCount = 0;
            _workerLabels = Array.Empty<string>();
            CustomMinimumSize = Vector2.Zero;
            QueueRedraw();
            return;
        }

        ulong minCycles = ulong.MaxValue;
        ulong maxCycles = ulong.MinValue;
        float minJobMs = float.MaxValue;
        foreach (var e in entries)
        {
            if (e.StartCycles < minCycles) minCycles = e.StartCycles;
            if (e.EndCycles > maxCycles) maxCycles = e.EndCycles;
        }

        double freq = System.Diagnostics.Stopwatch.Frequency;
        double ticksToMs = 1000.0 / freq;

        var barList = new List<Bar>(entries.Length);
        var workerSet = new HashSet<int>();

        foreach (var e in entries)
        {
            double elapsedCycles = (double)(e.EndCycles - e.StartCycles);
            float durationMs = (float)(elapsedCycles * ticksToMs);
            if (durationMs < MinJobDurationMs) continue;

            double offsetCycles = (double)(e.StartCycles - minCycles);
            float offsetMs = (float)(offsetCycles * ticksToMs);

            if (durationMs < minJobMs) minJobMs = durationMs;

            barList.Add(new Bar
            {
                Name = JobProfiler.GetJobName(e.JobNameHash),
                Hash = e.JobNameHash,
                OffsetMs = offsetMs,
                DurationMs = durationMs,
                WorkerIdx = e.ThreadIndex,
                JobType = e.JobType,
            });
            workerSet.Add(e.ThreadIndex);
        }

        if (barList.Count == 0)
        {
            _bars = Array.Empty<Bar>();
            _frameDurationMs = 16.7f;
            _workerCount = 0;
            _workerLabels = Array.Empty<string>();
            CustomMinimumSize = new Vector2(LeftMargin + _frameDurationMs * _pixelsPerMs + 40, HeaderHeight + 4);
            QueueRedraw();
            return;
        }

        _frameDurationMs = (float)((double)(maxCycles - minCycles) * ticksToMs);
        float minDisplayMs = Math.Max(_frameDurationMs, 16.7f) + 2f;
        _frameDurationMs = minDisplayMs;

        var sortedWorkers = new List<int>(workerSet);
        sortedWorkers.Sort();
        _workerCount = sortedWorkers.Count;
        _workerLabels = new string[_workerCount];
        var workerToRow = new Dictionary<int, int>();
        for (int i = 0; i < _workerCount; i++)
        {
            workerToRow[sortedWorkers[i]] = i;
            _workerLabels[i] = $"Worker {sortedWorkers[i]}";
        }

        for (int i = 0; i < barList.Count; i++)
        {
            var b = barList[i];
            b.WorkerIdx = workerToRow[b.WorkerIdx];
            barList[i] = b;
        }

        _bars = barList.ToArray();
        AutoZoom(minJobMs);
        UpdateContentSize();
        QueueRedraw();
    }

    private void AutoZoom(float minJobMs)
    {
        if (minJobMs <= 0.001f || minJobMs >= float.MaxValue)
        {
            _pixelsPerMs = _targetPxPerMs = 75f;
            return;
        }
        float desired = MinBarPxForText / minJobMs;
        desired = Math.Clamp(desired, 5f, 500f);
        _pixelsPerMs = _targetPxPerMs = desired;
    }

    private void UpdateContentSize()
    {
        float contentWidth = LeftMargin + _frameDurationMs * _pixelsPerMs + 40;
        float contentHeight = HeaderHeight + _workerCount * RowHeight + 4;
        CustomMinimumSize = new Vector2(contentWidth, contentHeight);
    }

    public override void _Draw()
    {
        float totalWidth = Math.Max(Size.X, CustomMinimumSize.X);
        float totalHeight = Math.Max(Size.Y, CustomMinimumSize.Y);

        DrawRect(new Rect2(Vector2.Zero, new Vector2(totalWidth, totalHeight)), _bgColor);
        if (_bars.Length == 0) return;

        DrawRect(new Rect2(0, 0, totalWidth, HeaderHeight), _headerBg);

        float plotLeft = LeftMargin;
        float plotWidth = totalWidth - LeftMargin;

        // ── 时间轴刻度 ──
        // 最多显示约 15 个刻度（无论放大多少倍），避免标签重叠
        int maxTicks = Math.Clamp((int)(plotWidth / MinTickPx), 1, 15);
        float tickMs = CalcTickInterval(_frameDurationMs, maxTicks);

        for (float tMs = 0; tMs <= _frameDurationMs + 0.01f; tMs += tickMs)
        {
            float x = plotLeft + tMs * _pixelsPerMs;
            if (x > totalWidth) break;

            DrawLine(new Vector2(x, HeaderHeight), new Vector2(x, totalHeight), _gridColor, 1f);

            string label = FormatMs(tMs);
            float textWidth = _font.GetStringSize(label, HorizontalAlignment.Left, -1, (int)FontSize).X;
            DrawString(_font, new Vector2(x - textWidth * 0.5f, HeaderHeight - 6), label,
                HorizontalAlignment.Left, -1, (int)FontSize, _timeAxisColor);
        }

        // ── Worker 行 ──
        float rowY = HeaderHeight;
        for (int r = 0; r < _workerCount; r++)
        {
            Color rowBg = (r % 2 == 0) ? _workerEven : _workerOdd;
            DrawRect(new Rect2(0, rowY, LeftMargin, RowHeight), rowBg);
            DrawString(_font, new Vector2(6, rowY + RowHeight - 7), _workerLabels[r],
                HorizontalAlignment.Left, -1, (int)FontSize, _textColor);
            rowY += RowHeight;
        }

        // ── Job 条 ──
        rowY = HeaderHeight;
        for (int i = 0; i < _bars.Length; i++)
        {
            var b = _bars[i];
            float x = plotLeft + b.OffsetMs * _pixelsPerMs;
            float w = Math.Max(b.DurationMs * _pixelsPerMs, 1f);
            float y = rowY + b.WorkerIdx * RowHeight + 2f;
            float h = RowHeight - 4f;

            Color color = GetJobColor(b.Hash);
            if (i == _hoveredBar)
            {
                DrawRect(new Rect2(x - 1, y - 1, w + 2, h + 2), Colors.White.Lightened(0.3f));
                DrawRect(new Rect2(x, y, w, h), color.Lightened(0.25f));
            }
            else
            {
                DrawRect(new Rect2(x, y, w, h), color);
            }

            if (w >= 14f)
            {
                string timeStr = $"{b.DurationMs:F3}ms";
                string nameStr = b.Name;

                float timeW = _font.GetStringSize(timeStr, HorizontalAlignment.Left, -1, (int)TinyFontSize).X;
                float nameW = _font.GetStringSize(nameStr, HorizontalAlignment.Left, -1, (int)TinyFontSize).X;

                if (nameW > w - 6)
                {
                    for (int ci = b.Name.Length - 1; ci >= 2; ci--)
                    {
                        nameStr = b.Name[..ci] + "..";
                        nameW = _font.GetStringSize(nameStr, HorizontalAlignment.Left, -1, (int)TinyFontSize).X;
                        if (nameW <= w - 6) break;
                    }
                }

                bool nameFits = (nameW <= w - 6);
                if (nameFits)
                {
                    float bothW = nameW + 4 + timeW;
                    if (bothW <= w - 6 && h >= 16)
                    {
                        float nameX = x + (w - nameW) * 0.5f;
                        float timeX = x + (w - timeW) * 0.5f;
                        DrawString(_font, new Vector2(nameX, y + h * 0.5f - 3), nameStr,
                            HorizontalAlignment.Left, -1, (int)TinyFontSize, Colors.White);
                        DrawString(_font, new Vector2(timeX, y + h * 0.5f + 8), timeStr,
                            HorizontalAlignment.Left, -1, (int)TinyFontSize, new Color(0.85f, 0.85f, 0.9f, 1f));
                        continue;
                    }
                    float dx1 = x + (w - nameW) * 0.5f;
                    DrawString(_font, new Vector2(dx1, y + (h - TinyFontSize) * 0.5f + 1), nameStr,
                        HorizontalAlignment.Left, -1, (int)TinyFontSize, Colors.White);
                }
                else if (timeW <= w - 6)
                {
                    float dx2 = x + (w - timeW) * 0.5f;
                    DrawString(_font, new Vector2(dx2, y + (h - TinyFontSize) * 0.5f + 1), timeStr,
                        HorizontalAlignment.Left, -1, (int)TinyFontSize, Colors.White);
                }
                else
                {
                    string shortTime = $"{b.DurationMs:F1}ms";
                    float sw = _font.GetStringSize(shortTime, HorizontalAlignment.Left, -1, (int)TinyFontSize).X;
                    if (sw > w - 6)
                    {
                        shortTime = $"{b.DurationMs:F0}ms";
                        sw = _font.GetStringSize(shortTime, HorizontalAlignment.Left, -1, (int)TinyFontSize).X;
                    }
                    float dx3 = x + (w - sw) * 0.5f;
                    DrawString(_font, new Vector2(dx3, y + (h - TinyFontSize) * 0.5f + 1), shortTime,
                        HorizontalAlignment.Left, -1, (int)TinyFontSize, Colors.White);
                }
            }
        }

        DrawLine(new Vector2(0, totalHeight - 1), new Vector2(totalWidth, totalHeight - 1),
            new Color(0.3f, 0.3f, 0.35f, 1f), 1f);
    }

    public override void _GuiInput(InputEvent @event)
    {
        // ── 只有有数据时才拦截轮（否则让 ScrollContainer 滚动） ──
        bool hasData = _bars.Length > 0;

        // ── 中键拖拽 ──
        if (@event is InputEventMouseButton mbMB)
        {
            if (mbMB.ButtonIndex == MouseButton.Middle)
            {
                if (mbMB.Pressed)
                {
                    _middleDragging = true;
                    _middleDragStart = mbMB.Position;
                    if (GetParent() is ScrollContainer sc)
                        _middleScrollStart = sc.ScrollHorizontal;
                    AcceptEvent();
                    return;
                }
                else
                {
                    _middleDragging = false;
                    AcceptEvent();
                    return;
                }
            }
        }

        if (@event is InputEventMouseMotion mmMB && _middleDragging)
        {
            if (GetParent() is ScrollContainer sc)
            {
                int deltaX = (int)(_middleDragStart.X - mmMB.Position.X);
                float maxScroll = Math.Max(0, CustomMinimumSize.X - sc.Size.X);
                sc.ScrollHorizontal = Math.Clamp(_middleScrollStart + deltaX, 0, (int)maxScroll);
            }
            AcceptEvent();
            return;
        }

        // ── 滚轮缩放 ──
        if (hasData && @event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            {
                if (mb.AltPressed) return; // 让 Alt+滚轮走默认
                if (_targetPxPerMs >= 499f) { AcceptEvent(); return; }

                float mouseMs = GetMouseTimeMs(mb.Position);
                float oldTarget = _targetPxPerMs;
                _targetPxPerMs = Math.Min(_targetPxPerMs * 1.15f, 500f);
                if (_targetPxPerMs <= oldTarget) { AcceptEvent(); return; }

                // 预先计算新滚动值
                float newPx = _targetPxPerMs;
                float newContentWidth = LeftMargin + _frameDurationMs * newPx + 40;
                float newScroll = mouseMs * newPx - (mb.Position.X - LeftMargin);

                _pixelsPerMs = newPx;
                UpdateContentSize();

                if (GetParent() is ScrollContainer sc)
                {
                    float viewportWidth = sc.Size.X;
                    float maxScroll = Math.Max(0, newContentWidth - viewportWidth);
                    sc.ScrollHorizontal = Math.Clamp((int)newScroll, 0, (int)maxScroll);
                }
                QueueRedraw();
                AcceptEvent();
                return;
            }

            if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            {
                if (_targetPxPerMs <= 0.51f) { AcceptEvent(); return; }

                float mouseMs = GetMouseTimeMs(mb.Position);
                float oldTarget = _targetPxPerMs;
                _targetPxPerMs = Math.Max(_targetPxPerMs / 1.15f, 0.5f);
                if (_targetPxPerMs >= oldTarget) { AcceptEvent(); return; }

                float newPx = _targetPxPerMs;
                float newContentWidth = LeftMargin + _frameDurationMs * newPx + 40;
                float newScroll = mouseMs * newPx - (mb.Position.X - LeftMargin);

                _pixelsPerMs = newPx;
                UpdateContentSize();

                if (GetParent() is ScrollContainer sc)
                {
                    float viewportWidth = sc.Size.X;
                    float maxScroll = Math.Max(0, newContentWidth - viewportWidth);
                    sc.ScrollHorizontal = Math.Clamp((int)newScroll, 0, (int)maxScroll);
                }
                QueueRedraw();
                AcceptEvent();
                return;
            }
        }

        // ── 悬停检测 ──
        if (@event is InputEventMouseMotion mm && !_middleDragging)
        {
            float mx = mm.Position.X;
            float my = mm.Position.Y;
            _hoveredBar = -1;

            if (hasData && my >= HeaderHeight)
            {
                int row = (int)((my - HeaderHeight) / RowHeight);
                if (row >= 0 && row < _workerCount)
                {
                    for (int i = 0; i < _bars.Length; i++)
                    {
                        var b = _bars[i];
                        if (b.WorkerIdx != row) continue;
                        float bx = LeftMargin + b.OffsetMs * _pixelsPerMs;
                        float bw = Math.Max(b.DurationMs * _pixelsPerMs, 1f);
                        float by = HeaderHeight + row * RowHeight;
                        if (mx >= bx && mx <= bx + bw && my >= by && my <= by + RowHeight)
                        {
                            _hoveredBar = i;
                            break;
                        }
                    }
                }
            }
            QueueRedraw();
        }
    }

    private float GetMouseTimeMs(Vector2 mousePos)
    {
        float scroll = 0;
        if (GetParent() is ScrollContainer sc) scroll = sc.ScrollHorizontal;
        float x = mousePos.X - LeftMargin + scroll;
        if (x < 0) x = 0;
        return x / _pixelsPerMs;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
        {
            UpdateContentSize();
            QueueRedraw();
        }
    }

    /// <summary>
    /// 选择刻度间隔：优先选能让 1ms 出现的较小间隔，
    /// 在不超过 maxTicks 的前提下选择最接近帧时长/15 的间隔。
    /// </summary>
    private static float CalcTickInterval(float frameDurationMs, int maxTicks)
    {
        if (frameDurationMs <= 0) return 1f;
        // 想要的刻度数量约为 maxTicks 的 70%
        int targetCount = Math.Max(1, (int)(maxTicks * 0.7f));
        float idealStep = frameDurationMs / targetCount;

        float[] candidates = {
            0.1f, 0.2f, 0.5f, 1f, 2f, 3f, 4f, 5f, 8f, 10f,
            13f, 16.7f, 20f, 25f, 33.3f, 50f, 100f, 200f, 500f
        };
        // 选择最接近 idealStep 的候选
        float best = candidates[^1];
        float bestDiff = float.MaxValue;
        foreach (var c in candidates)
        {
            int ticks = (int)(frameDurationMs / c) + 1;
            if (ticks <= maxTicks)
            {
                float diff = Math.Abs(c - idealStep);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = c;
                }
            }
        }
        return best;
    }

    private static string FormatMs(float ms)
    {
        if (ms >= 100f) return $"{ms:F0}ms";
        if (ms >= 10f) return $"{ms:F1}ms";
        if (ms >= 1f) return $"{ms:F2}ms";
        return $"{ms:F3}ms";
    }

    private static Color GetJobColor(ulong hash)
    {
        int idx = (int)(hash & 0x7FFFFFFF) % _palette.Length;
        return _palette[idx];
    }
}
