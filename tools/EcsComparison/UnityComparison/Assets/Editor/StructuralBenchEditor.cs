#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Entities;
using UnityEngine;
using Debug = UnityEngine.Debug;

// 结构变更对照臂：与 tools/EcsComparison 的 StructuralBench 同协议
// （10 万实体、按轮取最小、复位在计时之外），由 Unity 批处理 -executeMethod 调用。
public struct StructBenchA : IComponentData { public int Value; }
public struct StructBenchB : IComponentData { public int Value; }
public struct StructBenchC : IComponentData { public int Value; }

public static class StructuralBenchEditor
{
    public static void Run()
    {
        int entities = 100_000;
        int rounds = 25;
        string logPath = null;

        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-benchEntities" && int.TryParse(args[i + 1], out int parsedEntities)) entities = parsedEntities;
            else if (args[i] == "-benchRounds" && int.TryParse(args[i + 1], out int parsedRounds)) rounds = parsedRounds;
            else if (args[i] == "-benchLog") logPath = args[i + 1];
        }

        var output = new StringBuilder();
        output.AppendLine($"STRUCT_HEAD entities={entities} rounds={rounds} framework=Unity compiler=mono-editor opt=jit safety_checks=on");

        var world = new World("structural-bench");
        try
        {
            EntityManager em = world.EntityManager;
            EntityArchetype arch2 = em.CreateArchetype(typeof(StructBenchA), typeof(StructBenchB));
            EntityArchetype arch3 = em.CreateArchetype(typeof(StructBenchA), typeof(StructBenchB), typeof(StructBenchC));

            var buffer = new Entity[entities];
            var sw = new Stopwatch();
            double minCreate = double.MaxValue, minAdd = double.MaxValue;
            double minRemove = double.MaxValue, minDestroy = double.MaxValue;

            for (int r = 1; r <= rounds; r++)
            {
                sw.Restart();
                for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch2);
                sw.Stop();
                double us = sw.Elapsed.TotalMilliseconds * 1000.0;
                if (us < minCreate) minCreate = us;
                output.AppendLine($"STRUCT_ROW op=create framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");
                for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);

                for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch2);
                sw.Restart();
                for (int i = 0; i < entities; i++) em.AddComponent<StructBenchC>(buffer[i]);
                sw.Stop();
                us = sw.Elapsed.TotalMilliseconds * 1000.0;
                if (us < minAdd) minAdd = us;
                output.AppendLine($"STRUCT_ROW op=add framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");
                for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);

                for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch3);
                sw.Restart();
                for (int i = 0; i < entities; i++) em.RemoveComponent<StructBenchC>(buffer[i]);
                sw.Stop();
                us = sw.Elapsed.TotalMilliseconds * 1000.0;
                if (us < minRemove) minRemove = us;
                output.AppendLine($"STRUCT_ROW op=remove framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");
                for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);

                for (int i = 0; i < entities; i++) buffer[i] = em.CreateEntity(arch2);
                sw.Restart();
                for (int i = 0; i < entities; i++) em.DestroyEntity(buffer[i]);
                sw.Stop();
                us = sw.Elapsed.TotalMilliseconds * 1000.0;
                if (us < minDestroy) minDestroy = us;
                output.AppendLine($"STRUCT_ROW op=destroy framework=Unity round={r} us={us.ToString("F1", CultureInfo.InvariantCulture)}");

                output.AppendLine($"STRUCT_ROUND done round={r}");
                if (!string.IsNullOrEmpty(logPath)) File.WriteAllText(logPath, output.ToString());
            }

            output.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "STRUCT_MIN create={0:F1} add={1:F1} remove={2:F1} destroy={3:F1}",
                minCreate, minAdd, minRemove, minDestroy));
        }
        catch (Exception ex)
        {
            output.AppendLine("STRUCT_ERROR " + ex);
        }
        finally
        {
            world.Dispose();
        }

        if (!string.IsNullOrEmpty(logPath)) File.WriteAllText(logPath, output.ToString());
        Debug.Log(output.ToString());
    }
}
#endif
