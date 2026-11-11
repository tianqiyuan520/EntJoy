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

            // OrderBefore 与 OrderAfter 可能对同一对系统重复加边；重复边会让 inDegree 多加一次、出队只减一次，
            // 从而误判成环。
            var edgeSet = new HashSet<(int, int)>();
            void AddEdge(int from, int to)
            {
                if (from == to || !edgeSet.Add((from, to))) return;
                graph[from].Add(to);
                inDegree[to]++;
            }

            AddManualOrderEdges(AddEdge);
            AddConflictEdges(graph, AddEdge);
            LayerByKahn(graph, inDegree);
        }

        private void AddManualOrderEdges(Action<int, int> addEdge)
        {
            for (int i = 0; i < _systems.Count; i++)
            {
                foreach (var targetType in _systems[i].OrderBefore)
                {
                    int targetIdx = FindSystemIndex(targetType);
                    if (targetIdx >= 0) addEdge(i, targetIdx);
                }
                foreach (var targetType in _systems[i].OrderAfter)
                {
                    int targetIdx = FindSystemIndex(targetType);
                    if (targetIdx >= 0) addEdge(targetIdx, i);
                }
            }
        }

        // 组件冲突边：谁写谁阻塞谁，双向冲突按类型全名定方向，使执行顺序与注册顺序无关。
        private void AddConflictEdges(List<int>[] graph, Action<int, int> addEdge)
        {
            int n = _systems.Count;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (HasManualOrder(i, j)) continue;
                    if (!HasConflict(_systems[i], _systems[j])) continue;

                    // 手动顺序已建立可达关系时不再加自动边，否则可能与手动顺序互指成环。
                    if (IsReachable(i, j, graph) || IsReachable(j, i, graph)) continue;

                    bool iBlocksJ = _systems[i].WriteComponents.Overlaps(_systems[j].ReadComponents)
                                 || _systems[i].WriteComponents.Overlaps(_systems[j].WriteComponents);
                    bool jBlocksI = _systems[j].WriteComponents.Overlaps(_systems[i].ReadComponents)
                                 || _systems[j].WriteComponents.Overlaps(_systems[i].WriteComponents);
                    if (iBlocksJ && !jBlocksI) addEdge(i, j);
                    else if (jBlocksI && !iBlocksJ) addEdge(j, i);
                    else if (string.CompareOrdinal(_systems[i].SystemType.FullName, _systems[j].SystemType.FullName) <= 0)
                        addEdge(i, j);
                    else
                        addEdge(j, i);
                }
            }
        }

        // Kahn 分层。层内按 Order 排序，Order 相等时用类型全名做 tiebreak（List.Sort 不稳定）。
        private void LayerByKahn(List<int>[] graph, int[] inDegree)
        {
            int n = _systems.Count;
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (inDegree[i] == 0) queue.Enqueue(i);
            }

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
                        if (--inDegree[next] == 0) queue.Enqueue(next);
                    }
                }
                layer.Sort((a, b) =>
                {
                    int byOrder = a.Order.CompareTo(b.Order);
                    return byOrder != 0 ? byOrder : string.CompareOrdinal(a.SystemType.FullName, b.SystemType.FullName);
                });
                _layers.Add(layer);
            }

            // 排序后仍有入度未消的节点即为环：必须抛错，否则这些 system 永不执行且无任何提示。
            if (scheduled == n) return;
            var cyclic = new List<string>();
            for (int i = 0; i < n; i++)
            {
                if (inDegree[i] > 0) cyclic.Add(_systems[i].Name);
            }
            throw new InvalidOperationException(
                $"ScheduleGraph detected a cyclic dependency among systems: {string.Join(", ", cyclic)}.");
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