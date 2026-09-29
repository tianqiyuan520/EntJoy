using System;
using System.Collections.Generic;
using System.Linq;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // 对称读写冲突：Alpha 写 Vel 读 Pos；Beta 写 Pos 读 Vel ⇒ 两个方向都冲突。
    [Read(typeof(Position))] [Write(typeof(Velocity))]
    public struct SymAlpha { }

    [Read(typeof(Velocity))] [Write(typeof(Position))]
    public struct SymBeta { }

    // 同 Order、互不冲突（都只读）⇒ 层内顺序只能由确定性 tiebreak 决定。
    [Read(typeof(Position))]
    public struct IndepGamma { }

    [Read(typeof(Position))]
    public struct IndepDelta { }

    [Read(typeof(Position))]
    public struct IndepEpsilon { }

    /// <summary>
    /// R12：`ScheduleGraph` 的执行顺序不得依赖**注册顺序**。
    ///
    /// 旧实现两个来源：
    /// ① 自动冲突边的方向：i&lt;j 时"i 写与 j 读/写相交 ⇒ i→j"，对称冲突（A 写 X 读 Y / B 写 Y 读 X）
    ///    下换个注册顺序就得到相反的边 ⇒ 分层结果不同；
    /// ② 层内 `layer.Sort((a,b) =&gt; a.Order.CompareTo(b.Order))` 是**不稳定排序**，Order 相等时
    ///    顺序跟注册顺序走 ⇒ 同一组 system 的提交顺序不可复现（CI/不同机器上可能不同）。
    ///
    /// 判据：同一组 system 用两种注册顺序建图，拍平后的执行顺序必须**逐位相同**。
    /// </summary>
    public class ScheduleDeterminismTests
    {
        private static List<string> Flatten(ScheduleGraph g)
            => g.GetLayers().SelectMany(l => l.Select(s => s.Name)).ToList();

        private static ScheduleGraph Build(params Type[] order)
        {
            var g = new ScheduleGraph();
            foreach (var t in order)
                typeof(ScheduleGraph).GetMethod(nameof(ScheduleGraph.RegisterSystem))!
                    .MakeGenericMethod(t).Invoke(g, null);
            return g;
        }

        [Fact]
        public void SymmetricConflict_RegistrationOrder_MustNotChangeExecutionOrder()
        {
            var a = Flatten(Build(typeof(SymAlpha), typeof(SymBeta)));
            var b = Flatten(Build(typeof(SymBeta), typeof(SymAlpha)));

            Assert.Equal(a, b);
            Assert.Equal(2, a.Count);
        }

        [Fact]
        public void EqualOrderIndependentSystems_RegistrationOrder_MustNotChangeExecutionOrder()
        {
            var a = Flatten(Build(typeof(IndepGamma), typeof(IndepDelta), typeof(IndepEpsilon)));
            var b = Flatten(Build(typeof(IndepEpsilon), typeof(IndepGamma), typeof(IndepDelta)));

            Assert.Equal(a, b);
            Assert.Equal(new[] { nameof(IndepDelta), nameof(IndepEpsilon), nameof(IndepGamma) }, a);
        }
    }
}
