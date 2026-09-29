using System.Runtime.CompilerServices;

// 测试程序集可访问内部成员：用于对内部不变式（slab 账本、job 依赖表等）做断言，
// 避免为了可测性把它们提升为公共 API。
[assembly: InternalsVisibleTo("EntJoy.ECS.Tests")]
