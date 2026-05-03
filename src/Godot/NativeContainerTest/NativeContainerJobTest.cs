//using EntJoy.Collections;
//using Godot;
//using System;
//using System.Threading.Tasks;



//public partial class NativeContainerJobTest : Node
//{
//    public struct MyJob : IJob
//    {
//        public NativeArray<int> Output;
//        public NativeArray<int>.ReadOnly Input;

//        public void Execute()
//        {
//            try
//            {
//                GD.Print("运行MyJob");
//                for (int i = 0; i < Input.Length; i++)
//                {
//                    Output[i] = Input[i] * 2;
//                }
//                //GD.PrintS([.. Output.AsSpan()]);
//            }
//            catch (Exception ex)
//            {
//                GD.PrintErr($"MyJob failed: {ex}");
//            }
//        }
//    }

//    public struct OutJob : IJob
//    {
//        public NativeArray<int> Output;

//        public void Execute()
//        {
//            try
//            {
//                GD.Print("运行 OutJob- 输出");
//                GD.PrintS([.. Output.AsSpan()]);
//            }
//            catch (Exception ex)
//            {
//                GD.PrintErr($"OutJob failed: {ex}");
//            }

//        }
//    }




//    public override void _Ready()
//    {

//    }

//    public override void _PhysicsProcess(double delta)
//    {
//        TestJob();
//        CallDeferred(nameof(CleanupTemp));
//    }

//    private JobHandle dependency = new();


//    public void TestJob()
//    {
//        // 调度
//        var input = new NativeArray<int>(100, Allocator.Persistent);

//        var output = new NativeArray<int>(100, Allocator.Persistent);

//        for (int i = 0; i < 100; i++)
//        {
//            input[i] = i;
//            output[i] = 0;
//        }

//        var job = new MyJob { Input = input.AsReadOnly(), Output = output };
//        var job2 = new OutJob { Output = output };



//        //handle.Complete();
//        dependency = job2.Schedule(job.Schedule(dependency));
//        //handle.Complete();

//        input.Dispose(dependency);
//        output.Dispose(dependency);
//    }


//    private void CleanupTemp()
//    {
//        dependency.Complete();
//        TempAllocator.Reset();
//    }
//}
