using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NativeTranspiler.Analyzer.Common;

namespace NativeTranspiler.Analyzer
{
    /// <summary>原生构建工程（CMakeLists.txt）的发射：源清单、ISPC 规则与工具链选项。</summary>
    internal static class CmakeGenerator
    {
        // ISPC 对象同时依赖被 include 的帮助函数和类型定义。
        private static IEnumerable<string> GetIspcDependencies(string outputDir, string source)
        {
            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(source);
            while (pending.Count > 0)
            {
                string name = pending.Dequeue();
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, name));
                if (!visited.Add(fullPath) || !File.Exists(fullPath)) continue;
                yield return name;
                foreach (System.Text.RegularExpressions.Match include in System.Text.RegularExpressions.Regex.Matches(
                    File.ReadAllText(fullPath), @"(?m)^[ \t]*#[ \t]*include[ \t]+""([^""]+)"""))
                    pending.Enqueue(Path.Combine(Path.GetDirectoryName(name) ?? "", include.Groups[1].Value));
            }
        }

        public static string Generate(List<string> cppFiles, List<(string fileName, NativeTranspiler.IspcMathLib mathLib)> ispcFiles, HashSet<string> fastMathCppFiles, HashSet<string> autoSimdCppFiles,
                                  string outputDir, string outputBinDir, string relativeNativeDllDir, bool hasFastMath,
                                  List<string>? existingCppOrder = null, string? prebuiltNativeDir = null)
        {
            var sb = new StringBuilder();
            // 跨盘符时 GetRelativePath 返回绝对路径，再拼 ${CMAKE_CURRENT_SOURCE_DIR}/ 会得到
            // 无效路径（.../NativeTranspiler_Generated/C:/Users/.../.nuget/...）。绝对路径直接用。
            string cmakeNativeDllDir = Path.IsPathRooted(relativeNativeDllDir)
                ? relativeNativeDllDir
                : "${CMAKE_CURRENT_SOURCE_DIR}/" + relativeNativeDllDir;
            // prebuilt-native 模式（NuGet 包消费）：NativeDll 由包预编译提供，本工程只编 NativeTranspiled，
            // 因此不发射 add_library(NativeDll ...) / imgui / NativeDll 专属编译选项，改为链接包内导入库。
            bool prebuiltNative = !string.IsNullOrEmpty(prebuiltNativeDir);
            string prebuiltNativeLibRef = prebuiltNative
                ? Path.GetFullPath(Path.Combine(prebuiltNativeDir!, "NativeDll.lib")).Replace("\\", "/")
                : "";
            sb.AppendLine("cmake_minimum_required(VERSION 3.10)");
            sb.AppendLine("set(CMAKE_INSTALL_PREFIX \"${CMAKE_CURRENT_BINARY_DIR}/install\" CACHE PATH \"Install prefix\" FORCE)");
            sb.AppendLine("project(NativeDll LANGUAGES CXX)");
            sb.AppendLine();
            sb.AppendLine("set(CMAKE_CXX_STANDARD 20)");
            sb.AppendLine("set(CMAKE_CXX_STANDARD_REQUIRED ON)");
            sb.AppendLine();
            // Unity Build: merge multiple .cpp batches → 减少 ClangCL 启动开销的同时
            // 保留并行度。BATCH_SIZE=0（单 TU）在生成代码量大时（EntJoySample 207 cpp +
            // `cmake --build --parallel` 并行编译多 TU，增量也只重编变化的 TU。
            //
            // 但拆批有运行期代价：job 批函数调用的静态帮助函数（CpuOrca/CpuFlow/CpuObstacle/
            // CpuScan 的 static 方法）各自是独立 .cpp，拆批会把它们与调它的 job 分到不同 TU
            // 编译时间差距在小规模下可忽略（63 文件：单 TU 27s vs 批 8 25s）⇒ 小规模默认单 TU。
            sb.AppendLine("set(CMAKE_UNITY_BUILD ON)");
            // 决策：不分数量，任何规模都单 TU。
            // 为什么取消阈值：阈值是在回答一个"编译时间"问题，却在决定一个运行期问题 ——
            //   拆批会把生成内核与它调用的静态帮助函数（CpuOrca/CpuFlow/CpuObstacle/CpuScan 的 static，
            //   带 GENERATED_API=dllexport）分到不同 TU ⇒ 无 LTO ⇒ 不可内联 ⇒ 上面实测的 ~10% Melee 回归。
            //   （EntJoySample 207 cpp + 40 ispc：单 TU ~33s vs 26 TU 并行 ~1.6s）；
            //   ② 增量粒度退化为"整份"：改任何一个发射件都要重编整个 TU（本仓 169 文件约数十秒）。
            // ⇒ 将来若某工程编译时间不可接受，不要恢复阈值分支（那会再次静默回归 ~10% Melee）：
            //   正确做法是"按配置区分"（开发档拆批 / 基准与交付档单 TU），且改动必须带 Melee 段验收。
            sb.AppendLine("set(CMAKE_UNITY_BUILD_BATCH_SIZE 0)"
                + "   # 0 = 单 TU：让 job 与它调用的静态帮助函数同 TU 可内联（见上方实测）");
            sb.AppendLine("add_definitions(-DIMGUI_DEFINE_MATH_OPERATORS)");
            sb.AppendLine();
            sb.AppendLine("include_directories(${CMAKE_CURRENT_SOURCE_DIR})");
            sb.AppendLine($"include_directories(\"{cmakeNativeDllDir}\")");
            sb.AppendLine();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# CPU architecture detection");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(CMAKE_SYSTEM_PROCESSOR MATCHES \"^(x86_64|amd64|AMD64|x64)$\")");
            sb.AppendLine("    set(NATIVE_ARCH \"x86_64\")");
            sb.AppendLine("elseif(CMAKE_SYSTEM_PROCESSOR MATCHES \"^(aarch64|arm64|ARM64|AARCH64)$\")");
            sb.AppendLine("    set(NATIVE_ARCH \"arm64\")");
            sb.AppendLine("elseif(CMAKE_SYSTEM_PROCESSOR MATCHES \"^(armv[7-9]|arm|ARM)$\")");
            sb.AppendLine("    set(NATIVE_ARCH \"arm\")");
            sb.AppendLine("else()");
            sb.AppendLine("    set(NATIVE_ARCH \"unknown\")");
            sb.AppendLine("endif()");
            sb.AppendLine();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# SIMD level: AUTO or user override (-DNATIVE_SIMD_LEVEL=AVX2|SSE4|NEON|SCALAR)");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(NOT DEFINED NATIVE_SIMD_LEVEL)");
            sb.AppendLine("    set(NATIVE_SIMD_LEVEL \"AUTO\" CACHE STRING \"SIMD: AUTO/AVX2/AVX/SSE4/NEON/SCALAR\")");
            sb.AppendLine("endif()");
            sb.AppendLine("if(NATIVE_SIMD_LEVEL STREQUAL \"AUTO\")");
            sb.AppendLine("    if(NATIVE_ARCH MATCHES \"x86_64|x86\")");
            sb.AppendLine("        set(NATIVE_SIMD_LEVEL \"AVX2\")");
            sb.AppendLine("    elseif(NATIVE_ARCH MATCHES \"arm64|arm\")");
            sb.AppendLine("        set(NATIVE_SIMD_LEVEL \"NEON\")");
            sb.AppendLine("    else()");
            sb.AppendLine("        set(NATIVE_SIMD_LEVEL \"SCALAR\")");
            sb.AppendLine("    endif()");
            sb.AppendLine("endif()");
            sb.AppendLine("message(STATUS \"NativeDll: arch=${NATIVE_ARCH}, SIMD=${NATIVE_SIMD_LEVEL}\")");
            sb.AppendLine();

            sb.AppendLine("# No explicit task system defined; tasksys.cpp will pick the best one for the platform");
            sb.AppendLine($"if(EXISTS \"{cmakeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine($"    set(TASKSYS_SRC \"{cmakeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine("else()");
            sb.AppendLine("    set(TASKSYS_SRC \"\")");
            sb.AppendLine($"    message(WARNING \"tasksys.cpp not found at {cmakeNativeDllDir}/tasksys.cpp\")");
            sb.AppendLine("endif()");
            sb.AppendLine();

            // DLL 分离：NativeDll.dll（核心 runtime）+ NativeTranspiled.dll（生成代码）
            //   - NativeDll：JobSystem / WorkerPool / Profiler / Debugger / imgui / tasksys
            // 三个新 TU → 链接期 LNK2019（Scheduler/JobHandle 未定义）。glob 从根上消除该类回归。
            if (!prebuiltNative)
            {
                sb.AppendLine("# Core runtime (NativeDll.dll)");
                sb.AppendLine("add_library(NativeDll SHARED");
                var nativeDllAbsDir = Path.GetFullPath(Path.Combine(outputDir, relativeNativeDllDir));
                var nativeDllCppFiles = Directory.Exists(nativeDllAbsDir)
                    ? Directory.GetFiles(nativeDllAbsDir, "*.cpp")
                        .Select(f => Path.GetFileName(f))
                        .Where(f => !string.Equals(f, "tasksys.cpp", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();
                foreach (var f in nativeDllCppFiles)
                    sb.AppendLine($"    \"{cmakeNativeDllDir}/{f}\"");
                sb.AppendLine(")");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("# Core runtime (NativeDll.dll) is provided prebuilt by the NuGet package");
                sb.AppendLine();
            }
            sb.AppendLine("# Generated job wrappers + ISPC runtime (NativeTranspiled.dll)");
            sb.AppendLine("#   tasksys.cpp（ISPCAlloc/ISPCLaunch/ISPCSync 任务系统）必须与 ISPC 编译产物");
            sb.AppendLine("#   同 DLL：ISPC .obj 以普通符号引用 ISPCLaunch 等，MSVC 无法从另一 DLL");
            sb.AppendLine("#   自动导入普通符号（非 __imp_），故 tasksys + ISPC objects + 生成代码要同库。");
            sb.AppendLine("add_library(NativeTranspiled SHARED");
            foreach (var file in OrderCppSourcesStable(cppFiles, existingCppOrder))
            {
                // AutoSIMD 单元由 NativeTranspiledPrecise 对象库编译（无 fast-math），
                // 此处必须排除，避免同一符号在 fast-math 与 precise 两处重复定义（LNK2005）。
                if (autoSimdCppFiles.Contains(file)) continue;
                sb.AppendLine($"    {file}");
            }
            sb.AppendLine("    ${TASKSYS_SRC}");
            sb.AppendLine(")");
            sb.AppendLine(prebuiltNative
                ? $"target_link_libraries(NativeTranspiled PRIVATE \"{prebuiltNativeLibRef}\")"
                : "target_link_libraries(NativeTranspiled PRIVATE NativeDll)");
            sb.AppendLine();

            // 对象库保留全部按名称解析的导出，同时隔离精确浮点编译选项。
            //   454229d EC2/EC8/E5/E8/E11：AutoSIMD batch 控制流依赖 NaN/±0 精确语义，
            //   precise 库同样继承 CMAKE_UNITY_BUILD（与主库一致：单 TU）。
            if (autoSimdCppFiles.Count > 0)
            {
                // 确定性排序：precise 库的 unity 成员稳定（单 TU ⇒ 全库一个 TU，排序只为发射文本确定）。
                var autoSimdSorted = autoSimdCppFiles.OrderBy(f => f, StringComparer.Ordinal).ToList();
                sb.AppendLine("# --- AutoSIMD precise objects (fast-math OFF) ---");
                sb.AppendLine("set(AUTOSIMD_SOURCES");
                foreach (var file in autoSimdSorted)
                    sb.AppendLine($"    {file}");
                sb.AppendLine(")");
                sb.AppendLine("add_library(NativeTranspiledPrecise OBJECT ${AUTOSIMD_SOURCES})");
                sb.AppendLine("target_compile_options(NativeTranspiledPrecise PRIVATE");
                sb.AppendLine("    $<$<CXX_COMPILER_ID:MSVC>:/O2 /Ob2 /Oi /Ot /Qpar /MP>");      // MSVC default, no /fp:fast
                // ClangCL: 禁用自动 FMA 融合（clang: 前缀是 clang-cl 正确语法；裸 -ffp-contract 被忽略）。
                // 自动融合 a*a-3 → fmsub 单次舍入 vs C# 两次舍入，差异经除法放大可达 13 ULP
                // 生成器显式 n_fmadd_ps（增量 9）是 intrinsic 调用，不受 fp-contract 影响，性能保留。
                sb.AppendLine("    $<$<CXX_COMPILER_ID:Clang>:/O2 /Ob2 /Oi /Ot /Qpar /MP /clang:-ffp-contract=off>");
                sb.AppendLine("    $<$<NOT:$<CXX_COMPILER_ID:MSVC,Clang>>:-O3 -march=native -mtune=native -ffp-contract=off -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer>");
                sb.AppendLine(")");
                sb.AppendLine("target_compile_definitions(NativeTranspiledPrecise PRIVATE NDEBUG NOMINMAX GENERATED_EXPORTS)");
                sb.AppendLine("target_link_libraries(NativeTranspiled PRIVATE NativeTranspiledPrecise)");
                sb.AppendLine();
            }

            // 调试面板：Dear ImGui 集成（Windows + D3D11 后端）
            // 源码位于 NativeDll/thirdParty/imgui。Windows 上编译 imgui 核心 + Win32 + D3D11。
            // prebuilt 模式下 imgui 已编入包内 NativeDll，消费者不需要 imgui 源码/子模块。
            if (!prebuiltNative)
            {
                sb.AppendLine("# ============================================================");
                sb.AppendLine("# Dear ImGui debug panel (Windows / D3D11)");
                sb.AppendLine("# ============================================================");
                sb.AppendLine("if(WIN32)");
                sb.AppendLine($"    set(IMGUI_DIR   \"{cmakeNativeDllDir}/thirdParty/imgui\")");
                sb.AppendLine("    set(IMGUI_BACK  \"${IMGUI_DIR}/backends\")");
                sb.AppendLine("    target_include_directories(NativeDll PRIVATE ${IMGUI_DIR} ${IMGUI_BACK})");
                sb.AppendLine("    target_sources(NativeDll PRIVATE");
                sb.AppendLine("        ${IMGUI_DIR}/imgui.cpp");
                sb.AppendLine("        ${IMGUI_DIR}/imgui_draw.cpp");
                sb.AppendLine("        ${IMGUI_DIR}/imgui_tables.cpp");
                sb.AppendLine("        ${IMGUI_DIR}/imgui_widgets.cpp");
                sb.AppendLine("        ${IMGUI_BACK}/imgui_impl_win32.cpp");
                sb.AppendLine("        ${IMGUI_BACK}/imgui_impl_dx11.cpp");
                sb.AppendLine("    )");
                sb.AppendLine("    target_link_libraries(NativeDll PRIVATE d3d11 dxgi)");
                sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE ENTJOY_IMGUI_ENABLED=1)");
                sb.AppendLine("endif()");
                sb.AppendLine();
            }

            // SIMD arch flags + defines (after add_library)
            // 生成代码（NativeTranspiled）与核心（NativeDll）都 include Native Containes/Math
            // 头（含 SIMD），须应用相同的 SIMD 级别/数学精度/sentinel 布局，保证跨 DLL 一致。
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# SIMD arch flags + defines（NativeDll + NativeTranspiled 一致）");
            sb.AppendLine("# ============================================================");
            string simdTgtList = autoSimdCppFiles.Count > 0
                ? (prebuiltNative ? "NativeTranspiled NativeTranspiledPrecise" : "NativeDll NativeTranspiled NativeTranspiledPrecise")
                : (prebuiltNative ? "NativeTranspiled" : "NativeDll NativeTranspiled");
            sb.AppendLine($"foreach(SIMD_TGT {simdTgtList})");
            sb.AppendLine("    if(NATIVE_SIMD_LEVEL STREQUAL \"AVX2\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8)");
            sb.AppendLine("        if(MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE /arch:AVX2)");
            sb.AppendLine("        else()");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -mavx2 -mbmi2 -mfma)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    elseif(NATIVE_SIMD_LEVEL STREQUAL \"AVX\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_AVX NSIMD_WIDTH=8)");
            sb.AppendLine("        if(MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE /arch:AVX)");
            sb.AppendLine("        else()");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -mavx)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    elseif(NATIVE_SIMD_LEVEL STREQUAL \"SSE4\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_SSE4 NSIMD_WIDTH=4)");
            sb.AppendLine("        if(NOT MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -msse4.2)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    elseif(NATIVE_SIMD_LEVEL STREQUAL \"NEON\")");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_NEON NSIMD_WIDTH=4)");
            sb.AppendLine("        if(NOT MSVC)");
            sb.AppendLine("            target_compile_options(${SIMD_TGT} PRIVATE -march=armv8-a+simd)");
            sb.AppendLine("        endif()");
            sb.AppendLine("    else()");
            sb.AppendLine("        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_SCALAR NSIMD_WIDTH=1)");
            sb.AppendLine("    endif()");
            sb.AppendLine("    # SIMD math precision: 1=Fastest(~3.5ULP) 2=High(~1.0ULP) 3=IEEE(exact)");
            sb.AppendLine("    if(NOT DEFINED NATIVE_SIMD_MATH_PRECISION)");
            sb.AppendLine("        set(NATIVE_SIMD_MATH_PRECISION \"1\" CACHE STRING \"SIMD math precision level: 1=Fastest 2=High 3=IEEE\")");
            sb.AppendLine("    endif()");
            sb.AppendLine("    target_compile_definitions(${SIMD_TGT} PRIVATE SIMD_MATH_PRECISION=${NATIVE_SIMD_MATH_PRECISION})");
            sb.AppendLine("endforeach()");
            sb.AppendLine();

            // Sentinel: match C# #if DEBUG DisposeSentinel container layout.
            // 原生侧以 -DENTJOY_ENABLE_SENTINEL=ON 编译时 C++ 容器模板加 8B sentinel 字段对齐，
            // 否则 C# Debug + 原生适配器的 mirror static_assert 会失败（fail-fast）。Release 保持默认 OFF。
            sb.AppendLine("# Sentinel layout: match C# #if DEBUG DisposeSentinel (native templates add 8B sentinel field)");
            sb.AppendLine("option(ENTJOY_ENABLE_SENTINEL \"Match C# #if DEBUG DisposeSentinel container layout\" OFF)");
            sb.AppendLine("if(ENTJOY_ENABLE_SENTINEL)");
            if (!prebuiltNative)
                sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE ENTJOY_ENABLE_SENTINEL)");
            sb.AppendLine("    target_compile_definitions(NativeTranspiled PRIVATE ENTJOY_ENABLE_SENTINEL)");
            if (autoSimdCppFiles.Count > 0)
                sb.AppendLine("    target_compile_definitions(NativeTranspiledPrecise PRIVATE ENTJOY_ENABLE_SENTINEL)");
            sb.AppendLine("    message(STATUS \"NativeDll/NativeTranspiled: ENTJOY_ENABLE_SENTINEL ON (NativeArray=40B / NativeList=32B)\")");
            sb.AppendLine("endif()");
            sb.AppendLine();

            // ISPC: x86 only, optional
            if (ispcFiles.Count > 0)
            {
                sb.AppendLine("# ============================================================");
                sb.AppendLine("# ISPC: x86 only, optional");
                sb.AppendLine("# ============================================================");
                sb.AppendLine("find_program(ISPC_EXECUTABLE ispc)");
                sb.AppendLine("if(ISPC_EXECUTABLE AND NATIVE_ARCH MATCHES \"x86_64|x86\")");
                sb.AppendLine("    set(HAS_ISPC TRUE)");
                sb.AppendLine("    message(STATUS \"NativeDll: ISPC found\")");
                sb.AppendLine("else()");
                sb.AppendLine("    set(HAS_ISPC FALSE)");
                sb.AppendLine("endif()");
                sb.AppendLine();

                sb.AppendLine("if(HAS_ISPC)");
                sb.AppendLine("    set(ISPC_OBJECTS");
                foreach (var (ispc, _) in ispcFiles)
                {
                    string baseName = Path.GetFileNameWithoutExtension(ispc);
                    sb.AppendLine($"        \"${{CMAKE_CURRENT_BINARY_DIR}}/{baseName}.obj\"");
                }
                sb.AppendLine("    )");
                sb.AppendLine();
                foreach (var (ispc, mathLib) in ispcFiles)
                {
                    string baseName = Path.GetFileNameWithoutExtension(ispc);
                    string sourcePath = "${CMAKE_CURRENT_SOURCE_DIR}/" + ispc.Replace("\\", "/");
                    string objectPath = "${CMAKE_CURRENT_BINARY_DIR}/" + baseName + ".obj";
                    string headerPath = "${CMAKE_CURRENT_SOURCE_DIR}/" + baseName + "_ispc.h";
                    string mathLibStr = mathLib.ToString().ToLowerInvariant();
                    string fmaOpt = mathLib == NativeTranspiler.IspcMathLib.fast ? "" : " --opt=disable-fma";
                    sb.AppendLine("    add_custom_command(");
                    sb.AppendLine($"        OUTPUT \"{objectPath}\" \"{headerPath}\"");
                    sb.AppendLine($"        COMMAND \"${{ISPC_EXECUTABLE}}\" \"{sourcePath}\" -O3 --wrap-signed-int -o \"{objectPath}\" -h \"{headerPath}\" --target=avx2-i32x8 --math-lib={mathLibStr}{fmaOpt}");
                    var dependencies = GetIspcDependencies(outputDir, ispc)
                        .Select(path => "\"${CMAKE_CURRENT_SOURCE_DIR}/" + path.Replace("\\", "/") + "\"");
                    sb.AppendLine($"        DEPENDS {string.Join(" ", dependencies)}");
                    sb.AppendLine("        WORKING_DIRECTORY \"${CMAKE_CURRENT_SOURCE_DIR}\"");
                    sb.AppendLine($"        COMMENT \"Compiling ISPC {ispc}\"");
                    sb.AppendLine("        VERBATIM");
                    sb.AppendLine("    )");
                }
                sb.AppendLine("    set_source_files_properties(${ISPC_OBJECTS} PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)");
                // ISPC 对象由生成代码 wrapper 调用 → 链接进 NativeTranspiled？
                // 否——ISPC _impl 被生成代码 wrapper 直接函数调用，须在同一 DLL。
                // ISPC 编译产物是纯 avx 目标代码，与 NativeTranspiled 一起链接。
                sb.AppendLine("    target_sources(NativeTranspiled PRIVATE ${ISPC_OBJECTS})");
                sb.AppendLine();
                sb.AppendLine("    if(TASKSYS_SRC)");
                sb.AppendLine("        set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS \"/arch:AVX\")");
                sb.AppendLine("    endif()");
                sb.AppendLine("endif()");
                sb.AppendLine();
            }

            sb.AppendLine("# ============================================================");
            sb.AppendLine("# Global compiler flags");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(MSVC)");
            sb.AppendLine("    # 源文件为 UTF-8 无 BOM（含中文注释）。不加 /utf-8 时 MSVC 按本地代码页");
            sb.AppendLine("    # （中文系统=936/GBK）读取：UTF-8 汉字字节序列在 GBK 下可能被误判为含");
            sb.AppendLine("    # 0x5C 反斜杠 → 注释内触发行拼接吃掉下一行 → C4819 + C2065/C2447 级联解析错误。");
            sb.AppendLine("    if(CMAKE_CXX_COMPILER_ID STREQUAL \"Clang\")");
            sb.AppendLine("        # ClangCL (LLVM backend — faster SIMD than MSVC)");
            sb.AppendLine("        # /MP：Unity Build 拆批后同 project 内的多个 TU 并行编译");
            sb.AppendLine("        #（--parallel 只并行 project 间；缺 /MP 时拆批反而串行更慢）");
            // 内核禁展开：环体是「多流装载 + 条件存」的内存受限形态，展开抬高寄存器压力与溢出槽。
            // `-O3` 会重新打开展开器，必须显式 `-fno-unroll-loops`；调度器（NativeDll）保留展开，其环体短小。
            var kernelOpt = System.Environment.GetEnvironmentVariable("ENTJOY_KERNEL_O2") == "1"
                ? ""
                : " /clang:-O3 /clang:-ffp-contract=fast"
                  + " /clang:-fno-unroll-loops";
            var schedOpt = kernelOpt + " /clang:-funroll-loops";
            if (!prebuiltNative)
                sb.AppendLine("        target_compile_options(NativeDll PRIVATE /utf-8 /std:c++20 /O2 /Oi /fp:fast /MP" + schedOpt + ")");
            sb.AppendLine("        target_compile_options(NativeTranspiled PRIVATE /utf-8 /std:c++20 /O2 /Oi /fp:fast /MP" + kernelOpt + ")");
            // 【LTO A/B，`ENTJOY_LTO=1`，默认关】给 NativeTranspiled 开 LTO —— MSVC 的 `/GL`+`/LTCG` 等价物。
            // clang-cl 用 `-flto`（编译+链接）；本工程链接器已是 `lld-link`（见 build/CMakeCache.txt 的 CMAKE_LINKER），
            // 因此能直接吃 LLVM bitcode。动机：验证"跨 TU 的冗余指令/未内联助手能否被 LTO 消掉"。
            // ⚠ 本工程生成代码是单 TU（CMAKE_UNITY_BUILD + BATCH_SIZE 0），所有助手已头内联（`6a12cfe`）
            // ⇒ LTO 理论上无事可做；此开关用于把这件事实测钉死（见 ai）。
            if (System.Environment.GetEnvironmentVariable("ENTJOY_LTO") == "1")
            {
                sb.AppendLine("        target_compile_options(NativeTranspiled PRIVATE -flto)");
                sb.AppendLine("        set_target_properties(NativeTranspiled PROPERTIES LINK_FLAGS \"-flto\")");
            }
            sb.AppendLine("    else()");
            sb.AppendLine("        # MSVC (default)");
            if (!prebuiltNative)
                sb.AppendLine("        target_compile_options(NativeDll PRIVATE /utf-8 /std:c++20 /O2 /Ob2 /Oi /Ot /Qpar /MP /fp:fast)");
            sb.AppendLine("        target_compile_options(NativeTranspiled PRIVATE /utf-8 /std:c++20 /O2 /Ob2 /Oi /Ot /Qpar /MP /fp:fast)");
            // 附加编译器开关的实验旋钮（默认空 = 发射面逐字不变）。
            // 用途：在不改发射器的前提下，对生成 TU 做"构建期 A/B"（例如 `/Ob3`、`/favor:AMD64`）。
            // 注意 `target_compile_options` 是追加语义 ⇒ 后出现的同族开关覆盖前面的。
            string extraMsvc = System.Environment.GetEnvironmentVariable("ENTJOY_MSVC_EXTRA_FLAGS");
            if (!string.IsNullOrWhiteSpace(extraMsvc))
            {
                sb.AppendLine($"        # ENTJOY_MSVC_EXTRA_FLAGS");
                sb.AppendLine($"        target_compile_options(NativeTranspiled PRIVATE {extraMsvc!.Trim()})");
                if (!prebuiltNative)
                    sb.AppendLine($"        target_compile_options(NativeDll PRIVATE {extraMsvc!.Trim()})");
            }
            sb.AppendLine("    endif()");
            if (!prebuiltNative)
                sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE NDEBUG NOMINMAX NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)");
            sb.AppendLine("    # NativeTranspiled 导出生成代码 wrapper/adapter（GENERATED_API → dllexport）");
            sb.AppendLine("    target_compile_definitions(NativeTranspiled PRIVATE NDEBUG NOMINMAX GENERATED_EXPORTS)");
            sb.AppendLine("else()");
            if (!prebuiltNative)
                sb.AppendLine("    target_compile_options(NativeDll PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)");
            // NativeTranspiled keeps -ffast-math for performance; AutoSIMD precise lib (above)
            // compiles without -ffast-math to preserve 454229d's IEEE-754 semantics.
            sb.AppendLine("    target_compile_options(NativeTranspiled PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)");
            if (!prebuiltNative)
                sb.AppendLine("    target_compile_definitions(NativeDll PRIVATE NDEBUG NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)");
            sb.AppendLine("    target_compile_definitions(NativeTranspiled PRIVATE NDEBUG GENERATED_EXPORTS)");
            sb.AppendLine("endif()");
            sb.AppendLine();
            // 来解决同一个"跨 TU 不可内联"问题 —— 实测无收益（ΔMelee 从 −3.8±4.6 变到 −2.5±2.6ms，
            // 即在噪声内），而且 /GL 会拖慢链接。真正的解法是上面的 unity 批大小（单 TU），故此处不开 IPO。

            sb.AppendLine("# ============================================================");
            sb.AppendLine("# Platform-specific output suffix");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("if(WIN32)");
            if (!prebuiltNative)
                sb.AppendLine("    set_target_properties(NativeDll PROPERTIES SUFFIX \".dll\")");
            sb.AppendLine("    set_target_properties(NativeTranspiled PROPERTIES SUFFIX \".dll\")");
            sb.AppendLine("elseif(APPLE)");
            if (!prebuiltNative)
                sb.AppendLine("    set_target_properties(NativeDll PROPERTIES SUFFIX \".dylib\")");
            sb.AppendLine("    set_target_properties(NativeTranspiled PROPERTIES SUFFIX \".dylib\")");
            sb.AppendLine("else()");
            if (!prebuiltNative)
                sb.AppendLine("    set_target_properties(NativeDll PROPERTIES SUFFIX \".so\")");
            sb.AppendLine("    set_target_properties(NativeTranspiled PROPERTIES SUFFIX \".so\")");
            sb.AppendLine("endif()");
            sb.AppendLine();
            if (!prebuiltNative)
            {
                sb.AppendLine("set_target_properties(NativeDll PROPERTIES");
                sb.AppendLine("    RUNTIME_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
                sb.AppendLine("    LIBRARY_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
                sb.AppendLine("    ARCHIVE_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
                sb.AppendLine(")");
            }
            sb.AppendLine("set_target_properties(NativeTranspiled PROPERTIES");
            sb.AppendLine("    RUNTIME_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine("    LIBRARY_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine("    ARCHIVE_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_BINARY_DIR}\"");
            sb.AppendLine(")");
            return sb.ToString();
        }

        /// <summary>
        /// 读取上一次生成的 CMakeLists.txt 中 add_library(NativeTranspiled ...) 块内的源文件列表
        ///（保留原有顺序）。文件不存在或解析失败返回 null → 调用方回退到字典序。
        /// </summary>
        public static List<string>? ReadExistingCppSourceOrder(string cmakePath)
        {
            if (!File.Exists(cmakePath))
                return null;
            try
            {
                var order = new List<string>();
                bool inNativeTranspiled = false;
                foreach (var rawLine in File.ReadAllLines(cmakePath))
                {
                    var line = rawLine.Trim();
                    if (inNativeTranspiled)
                    {
                        if (line == ")") break;
                        // 形如 " SharpNative_Job_xxx_Execute.cpp"，排除 ${TASKSYS_SRC} 等变量
                        if (line.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) && !line.Contains("${"))
                            order.Add(line);
                    }
                    else if (line.StartsWith("add_library(NativeTranspiled", StringComparison.OrdinalIgnoreCase))
                    {
                        inNativeTranspiled = true;
                    }
                }
                return order.Count > 0 ? order : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 增量友好的源列表排序：保留 existingOrder（上一次 CMakeLists 的顺序）中仍存在于
        /// cppFiles 的文件，新文件按字典序追加到末尾，绝不打乱既有文件的相对顺序。
        /// native 侧只需重编新 TU + 末尾批。
        /// </summary>
        private static List<string> OrderCppSourcesStable(List<string> cppFiles, List<string>? existingCppOrder)
        {
            if (existingCppOrder == null || existingCppOrder.Count == 0)
                return cppFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            var ordered = new List<string>();
            var remaining = new HashSet<string>(cppFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var prev in existingCppOrder)
            {
                if (remaining.Remove(prev))
                    ordered.Add(prev);
            }
            // 新增文件（之前不存在）追加到末尾，避免插入中间打乱既有 Unity 批
            if (remaining.Count > 0)
                ordered.AddRange(remaining.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return ordered;
        }
    }
}
