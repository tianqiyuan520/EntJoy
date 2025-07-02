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
		debuggerPanel.Visible = true;
	}

	public void Close()
	{
		debuggerPanel.Visible = false;
		debuggerPanel.memoryGraphics.Visible = false;
	}
}
