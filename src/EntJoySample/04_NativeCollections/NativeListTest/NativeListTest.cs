//using EntJoy.Collections;

//public partial class NativeListTest
//{
//    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
//    public static void NativeListResize(NativeList<int> a)
//    {
//        a.Resize(10);
//    }

//    public unsafe static void Main()
//    {
//        NativeList<int> refValue = new(1);
//        refValue.Add(1);
//        Console.WriteLine("之前 容量 " + refValue.Capacity);
//        for (int i = 0; i < refValue.Length; i++) Console.WriteLine(refValue[i]);
//        NativeTranspiler.Bindings.NativeExports.NativeListResize(refValue);
//        Console.WriteLine("之后 容量 " + refValue.Capacity);
//        for (int i = 0; i < refValue.Length; i++) Console.WriteLine(refValue[i]);

//    }
//}