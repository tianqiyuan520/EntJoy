using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // 供 SystemRegistry 自动收集验证的计数器 System（OnUpdate 递增静态计数）
    [Read(typeof(Position))]
    public struct AutoCounterSystem : ISystem
    {
        public static int UpdateCount;
        public void OnUpdate() { UpdateCount++; }
    }

    // [DisableAutoCreation]：不参与 RegisterAll 自动收集，需手动注册
    [Read(typeof(Position))]
    [DisableAutoCreation]
    public struct ManualOnlySystem : ISystem
    {
        public static int UpdateCount;
        public void OnUpdate() { UpdateCount++; }
    }

    /// <summary>
    /// S27 System 注册生成：验证 SystemRegistry.RegisterAll 自动收集本程序集所有
    /// struct : ISystem 并一行注册（替代逐个 RegisterSystem&lt;T&gt;()）；
    /// [DisableAutoCreation] 标记的 System 被排除，需手动注册。
    /// </summary>
    public class SystemRegistrationGeneratorTests
    {
        [Fact]
        public void RegisterAll_RegistersAndRunsAllSystems()
        {
            AutoCounterSystem.UpdateCount = 0;
            using var world = new World("S27RegisterAll");
            var runner = new SystemRunner(world);

            // 一行注册本程序集所有 ISystem（含 AutoCounterSystem 与其他测试 System）
            SystemRegistry.RegisterAll(runner);

            runner.Update();

            // AutoCounterSystem 被自动注册且 OnUpdate 执行一次
            Assert.Equal(1, AutoCounterSystem.UpdateCount);
        }

        [Fact]
        public void RegisterAll_SkipsDisableAutoCreationSystems()
        {
            AutoCounterSystem.UpdateCount = 0;
            ManualOnlySystem.UpdateCount = 0;
            using var world = new World("S27DisableAutoCreation");
            var runner = new SystemRunner(world);

            SystemRegistry.RegisterAll(runner);   // 不包含 ManualOnlySystem
            runner.Update();

            // 自动收集的 System 执行了，[DisableAutoCreation] 的 System 未执行
            Assert.Equal(1, AutoCounterSystem.UpdateCount);
            Assert.Equal(0, ManualOnlySystem.UpdateCount);

            // 手动注册后正常执行
            runner.RegisterSystem<ManualOnlySystem>();
            runner.Update();
            Assert.Equal(1, ManualOnlySystem.UpdateCount);
        }
    }
}
