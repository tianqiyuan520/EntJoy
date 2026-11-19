using System;
using System.Runtime.CompilerServices;
using EntJoy.ECS.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests;

/// <summary>
/// chunk 上下文头布局契约：原生侧 NativeGeneratedSupport.h 用 static_assert 固定 x64 布局
/// （15 字段、88 字节），托管侧必须逐字段一致；指纹覆盖 名字 + 类型 + 偏移 + 宽度。
/// 任一侧字段增删/换序/换宽/同宽换类型都会让这组断言失败，强制两端同步。
/// </summary>
public class ChunkContextHeaderContractTests
{
    private const ulong ExpectedFingerprint = 14217895548820594514UL;

    [Fact]
    public void ManagedSizeMatchesNativeStaticAssert()
    {
        Assert.Equal(88, Unsafe.SizeOf<ChunkContextHeader>());
        Assert.Equal(88, System.Runtime.InteropServices.Marshal.SizeOf<ChunkContextHeader>());
    }

    [Fact]
    public void FieldFingerprintMatchesNativeHeader()
    {
        // 期望值由 src/NativeDll/NativeGeneratedSupport.h 的 __EntJoyChunkContextHeaderFingerprint()
        // 用 g++ 编出来的实测值（14217895548820594514）；改字段必须两端一起改并更新这个常量。
        Assert.Equal(ExpectedFingerprint, NativeChunkJobs.ComputeChunkContextHeaderFingerprint());
    }
}
