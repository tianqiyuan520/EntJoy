namespace EntJoy
{
    public static class World_Recorder
    {
        public static List<World> worldList = new() { };

        public static bool GetFirstWorld(out World? world)
        {
            world = worldList.Count > 0 ? worldList[0] : null;
            return world != null;
        }

        public static bool GetWorld(int index, out World? curworld)
        {
            curworld = worldList.Count > index ? worldList[index] : null;
            return curworld != null;
        }

        public static void RecordWorld(World curworld)
        {
            worldList.Add(curworld);
        }

        public static void RemoveWorld(int index)
        {
            if (worldList.Count <= index) return;
            worldList.RemoveAt(index);
        }
    }
}
