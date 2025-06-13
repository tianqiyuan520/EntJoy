using Godot;

public partial class CameraMove : Camera2D
{
	// Called when the node enters the scene tree for the first time.
	public float view_zoom = 1f;
	bool camera_draging = false;       //  相机是否拖拽
	Vector2 drag_start_camera_pos;
	Vector2 drag_start_mouse_pos;

	//  相机
	bool camera_drag = false;       //  相机是否拖拽
	Vector2 camera_old_pos;

	//  鼠标
	Vector2 mouse_pos;
	Vector2 mouse_screen_pos;
	Vector2 mouse_screen_old_pos;

	// 添加事件委托
	public delegate void ZoomChangedHandler(float newZoom);
	public event ZoomChangedHandler ZoomChanged;

	public override void _Process(double delta)
	{
		//  拖拽
		if (camera_drag)
		{
			Position = camera_old_pos - (mouse_screen_pos - mouse_screen_old_pos) / view_zoom;
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouse m)
		{
			mouse_pos = GetGlobalMousePosition();
			mouse_screen_pos = m.Position;

			if (m is InputEventMouseButton mb)        //  鼠标按键
			{
				var dif = 0.05f;
				if (mb.IsActionPressed("scroll_up") && view_zoom + dif < 2) //放大
				{
					view_zoom = view_zoom + dif;
					update_Zoom(view_zoom);
				}

				if (mb.IsActionPressed("scroll_down") && view_zoom - dif > 0.05) //缩小
				{
					view_zoom = view_zoom - dif;
					update_Zoom(view_zoom);
				}

				if (mb.IsActionPressed("scroll_click")) //移动
				{
					camera_drag = true;
					mouse_screen_old_pos = mouse_screen_pos;
					camera_old_pos = Position;
				}
				else { camera_drag = false; }
			}
		}
	}//  InputEventMouse

	void update_Zoom(float zoom_)
	{
		Zoom = new Vector2(zoom_, zoom_);
		//var tween = CreateTween();
		//tween.TweenProperty(this, "zoom", new Vector2(zoom_, zoom_), 0).FromCurrent();
		Position += -(GetGlobalMousePosition() - mouse_pos);     //  每次微小滚动造成的偏差都对齐回来
		ForceUpdateScroll();     //  马上更新
		ZoomChanged?.Invoke(zoom_);
	}

}
