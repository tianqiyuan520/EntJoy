using Godot;
using System;
using System.Diagnostics;

/// <summary>
/// 任务管理器风格 CPU 使用率图（0%~100%）。
/// 使用进程 CPU 增量 / 总核心数，等价于 Windows 任务管理器的"CPU"列。
/// 滚动窗口 200 帧。
/// 
/// 交互：
/// - 鼠标悬停 → 显示该帧 CPU% 详情
/// - 点击/拖拽白色竖线 → 选中帧，外部通过 CursorFrameIndex 跳转
/// </summary>
public partial class JobPerfGraph : Control
{
    public const int HistoryMax = 200;

    public struct FrameSample
    {
        public float CpuPercent;
        public int FrameId;
        public ulong PhysicsTick;
    }

    private FrameSample[] _samples = new FrameSample[HistoryMax];
    private int _head;

    private float _maxPercent = 100f;

    private int _cursorIndex = -1;
    private bool _draggingCursor;
    private int _hoverIndex = -1;

    private const float LeftMargin = 44f;
    private const float RightMargin = 10f;
    private const float BottomMargin = 18f;

    private static readonly Color _bgCol = new Color(0.08f, 0.08f, 0.10f, 1f);
    private static readonly Color _gridCol = new Color(0.18f, 0.18f, 0.20f, 1f);
    private static readonly Color _cpuCurveCol = new Color(0.35f, 0.85f, 0.55f, 1f);
    private static readonly Color _textCol = new Color(0.6f, 0.6f, 0.7f, 1f);
    private static readonly Color _cursorCol = new Color(1f, 1f, 1f, 0.85f);
    private static readonly Color _tooltipBg = new Color(0.12f, 0.12f, 0.14f, 0.92f);

    private Font _font;

    public int CursorAbsIndex => _cursorIndex;

    public int CursorFrameIndex
    {
        get
        {
            if (_cursorIndex < 0) return -1;
            int count = Math.Min(_head, HistoryMax);
            if (count == 0) return -1;
            int oldest = (_head - count + HistoryMax) % HistoryMax;
            int rel = (_cursorIndex - oldest + HistoryMax) % HistoryMax;
            return (rel >= 0 && rel < count) ? rel : -1;
        }
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Pass;
        _font = ThemeDB.FallbackFont ?? new FontFile();
        CustomMinimumSize = new Vector2(100, 100);
    }

    public void RecordFrame(float cpuPercent, int frameId, ulong tick)
    {
        int slot = _head % HistoryMax;
        _samples[slot] = new FrameSample
        {
            CpuPercent = Math.Clamp(cpuPercent, 0f, 100f),
            FrameId = frameId,
            PhysicsTick = tick,
        };
        _head++;

        // 更新 Y 轴最大值（固定 100%，不缩放）
        _maxPercent = 100f;

        QueueRedraw();
    }

    public bool SetCursorByFrameId(int frameId)
    {
        int count = Math.Min(_head, HistoryMax);
        if (count == 0) return false;

        for (int i = 0; i < count; i++)
        {
            int idx = (_head - count + i + HistoryMax) % HistoryMax;
            if (_samples[idx].FrameId == frameId)
            {
                _cursorIndex = idx;
                QueueRedraw();
                return true;
            }
        }
        return false;
    }

    public void SetCursorByRelIndex(int relIndex)
    {
        int count = Math.Min(_head, HistoryMax);
        if (count == 0 || relIndex < 0 || relIndex >= count) return;
        int oldest = (_head - count + HistoryMax) % HistoryMax;
        _cursorIndex = (oldest + relIndex) % HistoryMax;
        QueueRedraw();
    }

    public void Clear()
    {
        Array.Clear(_samples, 0, HistoryMax);
        _head = 0;
        _maxPercent = 100f;
        _cursorIndex = -1;
        _hoverIndex = -1;
        QueueRedraw();
    }

    private float PixelToSampleRel(float px)
    {
        float w = Math.Max(Size.X, CustomMinimumSize.X);
        float plotW = w - LeftMargin - RightMargin;
        if (plotW <= 1) return 0;
        return (px - LeftMargin) / plotW;
    }

    public override void _Draw()
    {
        float w = Math.Max(Size.X, CustomMinimumSize.X);
        float h = Math.Max(Size.Y, CustomMinimumSize.Y);
        float plotW = w - LeftMargin - RightMargin;
        float plotH = h - BottomMargin;

        DrawRect(new Rect2(Vector2.Zero, new Vector2(w, h)), _bgCol);
        if (plotW <= 10 || plotH <= 10) return;

        int count = Math.Min(_head, HistoryMax);
        if (count < 2) return;

        int oldest = (_head - count + HistoryMax) % HistoryMax;

        // ── 网格线（4等分） ──
        for (int i = 0; i <= 4; i++)
        {
            float y = plotH * (1f - i / 4f);
            DrawLine(new Vector2(LeftMargin, y), new Vector2(w - RightMargin, y), _gridCol, 1f);
            float pct = _maxPercent * i / 4f;
            string label = $"{pct:F0}%";
            float tw = _font.GetStringSize(label, HorizontalAlignment.Left, -1, 9).X;
            DrawString(_font, new Vector2(LeftMargin - tw - 3, y + 3), label,
                HorizontalAlignment.Left, -1, 9, _textCol);
        }

        // ── 曲线使用垂直条带填充替代 DrawPolygon，彻底避免 triangulation 崩溃 ──
        for (int i = 0; i < count; i++)
        {
            int idx = (oldest + i) % HistoryMax;
            float pct = Math.Min(_samples[idx].CpuPercent / _maxPercent, 1f);
            float x0 = LeftMargin + (i / (float)(count - 1)) * plotW;
            float stripW = Math.Max(plotW / (count - 1), 1f);
            float barH = plotH * pct;
            float barY = plotH - barH;

            Color fillCol = new Color(_cpuCurveCol.R, _cpuCurveCol.G, _cpuCurveCol.B, 0.15f);
            DrawRect(new Rect2(x0 - stripW * 0.4f, barY, stripW * 0.8f, barH), fillCol);
        }

        // ── 曲线折线 ──
        for (int i = 1; i < count; i++)
        {
            int idx0 = (oldest + i - 1) % HistoryMax;
            int idx1 = (oldest + i) % HistoryMax;
            float x0 = LeftMargin + ((i - 1) / (float)(count - 1)) * plotW;
            float x1 = LeftMargin + (i / (float)(count - 1)) * plotW;
            float y0 = plotH * (1f - Math.Min(_samples[idx0].CpuPercent / _maxPercent, 1f));
            float y1 = plotH * (1f - Math.Min(_samples[idx1].CpuPercent / _maxPercent, 1f));
            DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), _cpuCurveCol, 2f);
        }

        // ── 游标竖线 ──
        if (_cursorIndex >= 0)
        {
            int cursorRel = (_cursorIndex - oldest + HistoryMax) % HistoryMax;
            if (cursorRel >= 0 && cursorRel < count)
            {
                float cx = LeftMargin + (cursorRel / (float)(count - 1)) * plotW;
                DrawLine(new Vector2(cx, 0), new Vector2(cx, plotH), _cursorCol, 2f);
            }
        }

        // ── 悬停提示 ──
        if (_hoverIndex >= 0)
        {
            int hoverRel = (_hoverIndex - oldest + HistoryMax) % HistoryMax;
            if (hoverRel >= 0 && hoverRel < count)
            {
                var s = _samples[_hoverIndex];
                string tip = $"Frame {s.FrameId}\nCPU: {s.CpuPercent:F1}%";
                float tx = LeftMargin + (hoverRel / (float)(count - 1)) * plotW;
                float tw = _font.GetStringSize(tip, HorizontalAlignment.Left, -1, 10).X;
                float th = 28;
                float tipX = Math.Clamp(tx + 10, 4, w - tw - 8);
                float tipY = Math.Clamp(4f, 4, h - th - 8);

                DrawRect(new Rect2(tipX, tipY, tw + 8, th + 6), _tooltipBg);
                DrawRect(new Rect2(tipX, tipY, tw + 8, th + 6), new Color(0.3f, 0.3f, 0.35f, 0.6f), false, 1f);
                DrawString(_font, new Vector2(tipX + 4, tipY + 12), tip,
                    HorizontalAlignment.Left, -1, 10, Colors.White);
            }
        }

        // ── 最新值 ──
        if (count > 0)
        {
            var latest = _samples[(_head - 1 + HistoryMax) % HistoryMax];
            string topLabel = $"{latest.CpuPercent:F1}%";
            float lastX = LeftMargin + plotW;
            float lw = _font.GetStringSize(topLabel, HorizontalAlignment.Left, -1, 11).X;
            DrawString(_font, new Vector2(Math.Max(lastX - lw - 4, LeftMargin), 4), topLabel,
                HorizontalAlignment.Left, -1, 11, new Color(1f, 0.5f, 0.1f, 0.75f));
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        int count = Math.Min(_head, HistoryMax);
        if (count < 1) return;

        float w = Math.Max(Size.X, CustomMinimumSize.X);
        float plotW = w - LeftMargin - RightMargin;
        if (plotW <= 1) return;

        int oldest = (_head - count + HistoryMax) % HistoryMax;

        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
            {
                float rel = PixelToSampleRel(mb.Position.X);
                int idx = Math.Clamp((int)(rel * count), 0, count - 1);
                _cursorIndex = (oldest + idx) % HistoryMax;
                _draggingCursor = true;
                QueueRedraw();
                AcceptEvent();
                return;
            }
            if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
            {
                _draggingCursor = false;
                AcceptEvent();
                return;
            }
        }

        if (@event is InputEventMouseMotion mm)
        {
            if (_draggingCursor)
            {
                float rel = PixelToSampleRel(mm.Position.X);
                int idx = Math.Clamp((int)(rel * count), 0, count - 1);
                _cursorIndex = (oldest + idx) % HistoryMax;
                QueueRedraw();
                AcceptEvent();
                return;
            }

            float hRel = PixelToSampleRel(mm.Position.X);
            int hIdx = Math.Clamp((int)(hRel * count), 0, count - 1);
            int newHover = (oldest + hIdx) % HistoryMax;
            if (newHover != _hoverIndex)
            {
                _hoverIndex = newHover;
                QueueRedraw();
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit)
        {
            _hoverIndex = -1;
            _draggingCursor = false;
            QueueRedraw();
        }
    }
}
