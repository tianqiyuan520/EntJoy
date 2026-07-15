using Godot;

public partial class DebuggerTrigger : Button
{
    [Export]
    public PackedScene debuggerPanelPackedScene;
    [Export]
    public Node Pos;
    public DebuggerPanel debuggerPanel;
    public bool IsOpen = false;

    public override void _Ready()
    {
        Pressed += Press;

        if (debuggerPanelPackedScene != null && debuggerPanel == null)
        {
            debuggerPanel = debuggerPanelPackedScene.Instantiate<DebuggerPanel>();
            Pos?.AddChild(debuggerPanel);
        }
        if (debuggerPanel != null)
            debuggerPanel.Visible = false;
    }

    public void Press()
    {
        IsOpen = !IsOpen;
        if (IsOpen) Open();
        else Close();
    }

    public void Open()
    {
        if (debuggerPanel == null) return;
        debuggerPanel.Visible = true;
    }

    public void Close()
    {
        if (debuggerPanel == null) return;
        debuggerPanel.Visible = false;
        if (debuggerPanel.memoryGraphics != null)
            debuggerPanel.memoryGraphics.Visible = false;
    }
}
