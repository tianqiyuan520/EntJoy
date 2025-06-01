using Godot;

namespace EntJoy
{
    public class GodotAddonDebugger
    {
        public static void Send()
        {
            if (!Check()) return;
            EngineDebugger.SendMessage("EntJoyDebugger:the_data", ["132"]);
        }
        public static bool Check()
        {
            return EngineDebugger.IsActive();
            //var arr = GDExtensionManager.Singleton.GetLoadedExtensions();
            //foreach (var item in arr)
            //{
            //    GD.Print(item);
            //}
        }
    }
}
