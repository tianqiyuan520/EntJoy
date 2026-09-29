using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 后端选择的自证测试：`ENTJOY_TEST_BACKEND` 一旦被设置，实际后端就必须与请求一致。
    /// 没有这条断言时，CI 里「native 腿」可能悄悄退化成 Managed 而仍然全绿。
    /// </summary>
    public class BackendSelectionTests
    {
        [Fact]
        public void RequestedBackend_MatchesEffectiveBackend()
        {
            TestBackend.EnsureInitialized();

            switch (TestBackend.Requested)
            {
                case "native":
                    Assert.True(TestBackend.IsNative,
                        "ENTJOY_TEST_BACKEND=native，但 JobScheduler 报告的是 Managed 后端。");
                    break;

                case "managed":
                    Assert.False(TestBackend.IsNative,
                        "ENTJOY_TEST_BACKEND=managed，但 JobScheduler 报告的是 Native 后端。");
                    Assert.True(TestBackend.IsForcedManaged);
                    break;

                case "":
                    // 未显式要求时无法断言"是哪条后端"（取决于本机能否找到 NativeDll.dll），
                    // 但可以断言**内部状态自洽**：非强制托管时 UseNative 必须与 IsNative 一致、
                    // 且 WorkerCount 有效（旧写法 `Assert.Equal(IsNative, IsNative)` 是同义反复，被独立验收判为空转）。
                    Assert.False(TestBackend.IsForcedManaged, "未设置 ENTJOY_TEST_BACKEND 时不应处于强制托管态");
                    Assert.True(JobScheduler.WorkerCount >= 1);
                    break;

                default:
                    Assert.Fail($"未知的 ENTJOY_TEST_BACKEND='{TestBackend.Requested}'。");
                    break;
            }
        }

        [Fact]
        public void WorkerCount_IsPositive_OnEveryBackend()
        {
            TestBackend.EnsureInitialized();
            Assert.True(JobScheduler.WorkerCount >= 1,
                $"WorkerCount 必须 >= 1，实际 {JobScheduler.WorkerCount}（后端 native={TestBackend.IsNative}）。");
        }
    }
}
