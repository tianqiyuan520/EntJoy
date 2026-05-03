//public class ISPCMTTest
//{
//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, UseISPC_MT = true)]
//    public unsafe static void Test(int* c)
//    {
//        for (int i = 0; i < 100; i++) Interlocked.Increment(ref *c);
//        Interlocked.Increment(ref *c);
//        for (int i = 0; i < 100; i++) Interlocked.Increment(ref *c);
//    }


//    public unsafe static void Main(string[] args)
//    {
//        int c = 0;
//        NativeTranspiler.Bindings.NativeExports.Test(&c);
//        Console.WriteLine(c);

//    }
//}

