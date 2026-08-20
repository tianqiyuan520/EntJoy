//using EntJoy.Collections;
//using Godot;

//public partial class NativeContainerTest : Node
//{
//    //判断 泄露问题
//    private void TestLeak()
//    {
//        var list = new NativeList<int>(10, Allocator.Persistent);
//        for (int i = 0; i < 100; i++)
//            list.Add(i);
//        list.Insert(50, 999);
//        list.RemoveAt(10);
//        list.RemoveAtSwapBack(20);

//        GD.PrintS([.. list.AsSpan()]);
//        list.Dispose();
//    }

//    // 判断安全性
//    private void TestSafety()
//    {
//        var array = new NativeArray<int>(100, Allocator.Temp);
//        for (int i = 0; i < array.Length; i++) array[i] = i;

//        var readOnly = array.AsReadOnly();
//        int x = readOnly[50]; //
//        //readOnly[50] = 999; //


//        array.Dispose();
//    }

//    private unsafe struct ResizeJob : IJob
//    {
//        public NativeList<int> list;

//        public void Execute()
//        {
//            list.Resize(20, NativeArrayOptions.ClearMemory);
//        }
//    }

//    private unsafe void ResizeTest()
//    {
//        var list = new NativeList<int>(10, Allocator.Persistent);
//        var handle = new ResizeJob
//        {
//            list = list,
//        }.Schedule();
//        //list.Resize(20, NativeArrayOptions.ClearMemory); //ok
//        list.Dispose(handle);
//    }

//    public override void _Ready()
//    {
//        //TestLeak(); // list
//        //TestSafety();
//        ResizeTest();

//        // 强制 GC 并等待终结器
//        //GC.Collect();
//        //GC.WaitForPendingFinalizers();
//        //GC.Collect();
//    }

//    public override void _Process(double delta)
//    {
//        CallDeferred(nameof(CleanupTemp));

//    }

//    private void CleanupTemp()
//    {
//        TempAllocator.Reset();
//    }
//}