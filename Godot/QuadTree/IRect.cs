using EntJoy;

/// <summary>
/// 范围接口
/// </summary>
public interface IRect
{
    /// <summary>
    /// 矩形中心x
    /// </summary>
    public float X { get; set; }
    /// <summary>
    /// 矩形中心y
    /// </summary>
    public float Y { get; set; }
    /// <summary>
    /// 矩形宽度
    /// </summary>
    public float Width { get; set; }
    /// <summary>
    /// 矩形高度
    /// </summary>
    public float Height { get; set; }

}

public struct QTComp : IRect,IComponent
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}

    