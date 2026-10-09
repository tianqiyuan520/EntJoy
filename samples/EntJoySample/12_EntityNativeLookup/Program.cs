//using EntJoy.JobSystem;
//using EntJoySample.NativeLookup;
//using EntJoySample.EnableBitmap;
//using EntJoySample.AutoSimdBulkWriteback;

//namespace EntJoySample.NativeLookup.Entry
//{
//    /// <summary>
//    /// 框架原生能力验收入口（12 + 13 + 14 三段）：
//    ///   12_EntityNativeLookup  —— //（定位表、批量创建销毁、ECB）
//    ///   13_EnableBitMapNative  —— （原生 IJobChunk 读/写逐组件 enable 位图）+
//    ///   14_AutoSimdChunkWriteback —— （IJobChunk 双组件整结构体回写，现状沉淀坑 2）
//    /// ⚠ 本工程约定「仅保留一个非注释 Main」：
//    ///   启用本入口时，需把 `01_JobSystem/ParallelRwConflictTest/Program.cs` 的 Main 注释掉（反向同理）。
//    /// </summary>
//    public static class Program
//    {
//        public static void Main()
//        {
//            // ECS 的 Schedule 路径需要原生 worker（C++ Chase-Lev 调度器）
//            JobScheduler.Initialize();

//            EntityNativeLookupDemo.Run();
//            EnableBitMapNativeDemo.Run();
//            AutoSimdChunkWritebackDemo.Run();
//        }
//    }
//}
