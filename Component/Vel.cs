namespace EntJoy
{
    /// <summary>
    /// 速度组件
    /// </summary>
    public struct Vel : IComponent
    {
        public float x;
        public float y;

        public Vel(float x, float y)
        {
            this.x = x;
            this.y = y;
        }
    }
}
