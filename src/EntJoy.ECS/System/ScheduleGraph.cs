using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace EntJoy.ECS
{
    public struct SystemSlot
    {
        public Type SystemType;
        public HashSet<Type> ReadComponents;
        public HashSet<Type> WriteComponents;
        public int Order;
        public string Name;
        public List<Type> OrderBefore;
        public List<Type> OrderAfter;
    }

    public class ScheduleGraph
    {
        private readonly List<SystemSlot> _systems = new();
        private List<List<SystemSlot>> _layers = new();

        public void RegisterSystem<T>() where T : struct
        {
            var systemType = typeof(T);
            var slot = new SystemSlot
            {
                SystemType = systemType,
                ReadComponents = new HashSet<Type>(),
                WriteComponents = new HashSet<Type>(),
                Order = GetOrderPriority(systemType),
                Name = systemType.Name,
                OrderBefore = new List<Type>(),
                OrderAfter = new List<Type>()
            };

            foreach (var attr in systemType.GetCustomAttributes(typeof(ReadAttribute), true))
            {
                var readAttr = (ReadAttribute)attr;
                foreach (var ct in readAttr.ComponentTypes)
                    slot.ReadComponents.Add(ct);
            }

            foreach (var attr in systemType.GetCustomAttributes(typeof(WriteAttribute), true))
            {
                var writeAttr = (WriteAttribute)attr;
                foreach (var ct in writeAttr.ComponentTypes)
                    slot.WriteComponents.Add(ct);
            }

            foreach (var attr in systemType.GetCustomAttributes(typeof(OrderBeforeAttribute), true))
                slot.OrderBefore.Add(((OrderBeforeAttribute)attr).TargetSystem);

            foreach (var attr in systemType.GetCustomAttributes(typeof(OrderAfterAttribute), true))
                slot.OrderAfter.Add(((OrderAfterAttribute)attr).TargetSystem);

            _systems.Add(slot);
            RebuildGraph();
        }

        private void RebuildGraph()
        {
            _layers = new List<List<SystemSlot>>();
            int n = _systems.Count;
            if (n == 0) return;

            var graph = new List<int>[n];
            var inDegree = new int[n];
            for (int i = 0; i < n; i++) graph[i] = new List<int>();

            // 边去重：OrderBefore 与 OrderAfter 可能对同一对系统重复加边（如 A.OrderBefore[B] 且 B.OrderAfter[A]），
            // 重复边会使 inDegree 多次递增但出队只减一次 → 误判环。用 HashSet 记录已加边。
            var edgeSet = new HashSet<(int, int)>();

            void AddEdge(int from, int to)
            {
                if (from == to || !edgeSet.Add((from, to))) return;
                graph[from].Add(to);
                inDegree[to]++;
            }

            for (int i = 0; i < n; i++)
            {
                foreach (var targetType in _systems[i].OrderBefore)
                {
                    int targetIdx = FindSystemIndex(targetType);
                    if (targetIdx >= 0) AddEdge(i, targetIdx);
                }
                foreach (var targetType in _systems[i].OrderAfter)
                {
                    int targetIdx = FindSystemIndex(targetType);
                    if (targetIdx >= 0) AddEdge(targetIdx, i);
                }
            }

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (HasManualOrder(i, j)) continue;
                    if (HasConflict(_systems[i], _systems[j]))
                    {
                        // 已达可达关系（手动顺序的传递闭包，如 First→Middle→Last）时不再加自动边，
                        // 否则自动冲突边可能与手动顺序形成环（First→Middle→Last→First）误报 cyclic dependency。
                        if (IsReachable(i, j, graph) || IsReachable(j, i, graph)) continue;
                        bool iBlocksJ = _systems[i].WriteComponents.Overlaps(_systems[j].ReadComponents)
                                     || _systems[i].WriteComponents.Overlaps(_systems[j].WriteComponents);
                        bool jBlocksI = _systems[j].WriteComponents.Overlaps(_systems[i].ReadComponents)
                                     || _systems[j].WriteComponents.Overlaps(_systems[i].WriteComponents);
                        if (iBlocksJ && !jBlocksI)
                        {
                            AddEdge(i, j);
                        }
                        else if (jBlocksI && !iBlocksJ)
                        {
                            AddEdge(j, i);
                        }
                        else
                        {
                            // 双向冲突（A 写 X 读 Y / B 写 Y 读 X）与写-写冲突时，旧实现按
                            //   注册顺序（i<j 就取 i→j）定边 ⇒ 同一组 system 换个注册顺序就得到
                            //   不同的执行顺序（提交顺序不可复现）。改为按类型全名的稳定判据定方向。
                            if (string.CompareOrdinal(_systems[i].SystemType.FullName, _systems[j].SystemType.FullName) <= 0)
                                AddEdge(i, j);
                            else
                                AddEdge(j, i);
                        }
                    }
                }
            }

            var queue = new Queue<int>();
            for (int i = 0; i < n; i++) { if (inDegree[i] == 0) queue.Enqueue(i); }
            int scheduled = 0;
            while (queue.Count > 0)
            {
                var layer = new List<SystemSlot>();
                int layerSize = queue.Count;
                scheduled += layerSize;
                for (int k = 0; k < layerSize; k++)
                {
                    int idx = queue.Dequeue();
                    layer.Add(_systems[idx]);
                    foreach (int next in graph[idx])
                    {
                        inDegree[next]--;
                        if (inDegree[next] == 0) queue.Enqueue(next);
                    }
                }
                // 同 layer 内按 Order 优先级排序（Order 越小越先执行）。
                // Order 相等时必须有与注册顺序无关的 tiebreak（List.Sort 是不稳定排序，
                //   否则同 Order 的提交顺序会跟着注册顺序走 ⇒ 不可复现）。
                layer.Sort((a, b) =>
                {
                    int byOrder = a.Order.CompareTo(b.Order);
                    return byOrder != 0
                        ? byOrder
                        : string.CompareOrdinal(a.SystemType.FullName, b.SystemType.FullName);
                });
                _layers.Add(layer);
            }

            // 环检测：Kahn 排序后仍有 inDegree>0 的节点即为循环依赖（OrderBefore/OrderAfter 互指），
            // 直接抛错而非静默丢弃——否则这些 system 永不执行且无任何提示。
            if (scheduled != n)
            {
                var cyclic = new List<string>();
                for (int i = 0; i < n; i++)
                    if (inDegree[i] > 0) cyclic.Add(_systems[i].Name);
                throw new InvalidOperationException(
                    $"ScheduleGraph detected a cyclic dependency among systems: {string.Join(", ", cyclic)}.");
            }
        }

        private bool HasManualOrder(int i, int j)
        {
            var si = _systems[i]; var sj = _systems[j];
            if (si.OrderBefore.Contains(sj.SystemType)) return true;
            if (sj.OrderBefore.Contains(si.SystemType)) return true;
            if (si.OrderAfter.Contains(sj.SystemType)) return true;
            if (sj.OrderAfter.Contains(si.SystemType)) return true;
            return false;
        }

        /// <summary>检查 graph 中 from 能否到达 to（DFS）。</summary>
        private static bool IsReachable(int from, int to, List<int>[] graph)
        {
            var visited = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                if (cur == to) return true;
                if (!visited.Add(cur)) continue;
                foreach (var next in graph[cur])
                    stack.Push(next);
            }
            return false;
        }

        private int FindSystemIndex(Type systemType)
        {
            for (int i = 0; i < _systems.Count; i++)
                if (_systems[i].SystemType == systemType) return i;
            return -1;
        }

        public void PrintSchedule()
        {
            Console.WriteLine("=== Schedule Graph DAG ===");
            for (int i = 0; i < _layers.Count; i++)
            {
                var layer = _layers[i];
                var names = string.Join(", ", layer.Select(s => s.Name));
                var reads = string.Join(", ", layer.SelectMany(s => s.ReadComponents).Select(t => t.Name).Distinct());
                var writes = string.Join(", ", layer.SelectMany(s => s.WriteComponents).Select(t => t.Name).Distinct());
                // 同 layer 系统间无冲突（可并行），但当前 SystemRunner 按 Order 串行执行——标记为 "no-conflict" 避免误读为已并行。
                if (layer.Count > 1) Console.WriteLine($"  Layer {i}: [{names}] (no-conflict)");
                else Console.WriteLine($"  Layer {i}: {names}");
                Console.WriteLine($"    Read:  [{reads}]");
                Console.WriteLine($"    Write: [{writes}]");
                var befores = string.Join(", ", layer.SelectMany(s => s.OrderBefore).Select(t => t.Name).Distinct());
                var afters = string.Join(", ", layer.SelectMany(s => s.OrderAfter).Select(t => t.Name).Distinct());
                if (!string.IsNullOrEmpty(befores) || !string.IsNullOrEmpty(afters))
                {
                    var c = new List<string>();
                    if (!string.IsNullOrEmpty(befores)) c.Add($"Before: [{befores}]");
                    if (!string.IsNullOrEmpty(afters)) c.Add($"After: [{afters}]");
                    Console.WriteLine($"    Order: {string.Join(", ", c)}");
                }
            }
            Console.WriteLine("==========================\n");
        }

        public List<List<SystemSlot>> GetLayers() => _layers;

        private bool HasConflict(SystemSlot a, SystemSlot b)
        {
            if (a.WriteComponents.Overlaps(b.ReadComponents)) return true;
            if (a.ReadComponents.Overlaps(b.WriteComponents)) return true;
            if (a.WriteComponents.Overlaps(b.WriteComponents)) return true;
            return false;
        }

        private int GetOrderPriority(Type systemType)
        {
            var attr = systemType.GetCustomAttribute<OrderAttribute>();
            return attr?.Priority ?? 0;
        }
    }
}