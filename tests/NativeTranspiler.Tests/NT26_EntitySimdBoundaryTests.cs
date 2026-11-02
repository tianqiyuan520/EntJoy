using Microsoft.CodeAnalysis;
using Xunit;

namespace NativeTranspiler.Tests
{
    public class NT26_EntitySimdBoundaryTests
    {
        private static EmitResult Emit(string mode) => GeneratorHarness.EmitFor(@"
using EntJoy.ECS; using NativeTranspiler;
public struct Pose { public float X; }
[NativeTranspile(AutoSIMD = AutoSIMD." + mode + @")]
public struct EntityModeJob : IJobEntity {
 public void Execute(ref Pose value) { value.X += 1; }
}");

        [Fact]
        public void EnabledMode_ReportsItsScalarImplementation()
            => Assert.True(Emit("Enabled").HasDiagnostic("NT031"));

        [Theory]
        [InlineData("Disabled")]
        [InlineData("Vectorize")]
        public void ExistingEntityModes_StayAvailable(string mode)
            => Assert.DoesNotContain(Emit(mode).Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }
}
