using Godot;

public partial class CameraMove : Camera2D
{
    public float view_zoom = 1f;

    //  相机拖拽状态
    bool camera_drag = false;
    Vector2 camera_old_pos;

    //  鼠标
    Vector2 mouse_screen_pos;
    Vector2 mouse_screen_old_pos;

    // 事件委托
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
            mouse_screen_pos = m.Position;

            if (m is InputEventMouseButton mb)        //  鼠标按键
            {
                var dif = 0.02f;
                if (mb.IsActionPressed("scroll_up") && view_zoom + dif < 2) //缩小
                {
                    view_zoom = view_zoom + dif;
                    update_Zoom(view_zoom);
                }
                else if (mb.IsActionPressed("scroll_down") && view_zoom - dif > 0.04) //放大
                {
                    view_zoom = view_zoom - dif;
                    update_Zoom(view_zoom);
                }
                else if (mb.ButtonIndex == MouseButton.Middle) //中键
                {
                    if (mb.IsPressed()) //按下开始拖拽
                    {
                        camera_drag = true;
                        mouse_screen_old_pos = mouse_screen_pos;
                        camera_old_pos = Position;
                    }
                    else //释放停止拖拽
                    {
                        camera_drag = false;
                    }
                }
                // 其他按键不改变拖拽状态
            }
        }
    }//  InputEventMouse

    void update_Zoom(float zoom_)
    {
        Zoom = new Vector2(zoom_, zoom_);
        // 保持光标位置不动（Godot anchor 方式）
        ForceUpdateScroll();
        ZoomChanged?.Invoke(zoom_);
    }

}
