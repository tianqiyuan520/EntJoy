using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using EntJoy.Collections;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        // 内存分析

        /// <summary>
        /// 生成内存分析报告（纯观测快照）：原生分配/释放/泄漏、Chunk 数、碎片率、slab 占用。
        /// </summary>
        public MemoryReport GetMemoryReport(float thinThresholdPercent = 30f)
        {
            var alloc = PersistentAllocator.GetStats();
            var report = new MemoryReport
            {
                NativeAllocs = alloc.Allocs,
                NativeFrees = alloc.Frees,
                NativeHits = alloc.Hits,
                NativeMisses = alloc.Misses,
                NativeForeign = alloc.Foreign,
                LeakedContainers = DisposeSentinel.LeakedCount,
                TotalEntityCount = LiveEntityCount,
                Archetypes = new List<ArchetypeMemoryInfo>(),
            };

            for (int i = 0; i < archetypeCount; i++)
            {
                var arch = allArchetypes[i];
                if (arch == null) continue;
                report.TotalChunkCount += arch.ChunkCount;
                report.TotalSlabBytes += arch.SlabBytes;
                report.ThinChunkCount += CountThinChunks(arch, thinThresholdPercent);
                report.Archetypes.Add(DescribeArchetype(arch));
            }
            return report;
        }

        // 瘦块（利用率低于阈值）是碎片信号，空块不算。
        private static int CountThinChunks(Archetype arch, float thinThresholdPercent)
        {
            int thin = 0;
            foreach (var chunk in arch.ChunkList)
                if (chunk.EntityCount > 0 && chunk.EntityCount * 100f < chunk.Capacity * thinThresholdPercent) thin++;
            return thin;
        }

        private static ArchetypeMemoryInfo DescribeArchetype(Archetype arch)
        {
            var names = new string[arch.ComponentCount];
            for (int c = 0; c < arch.ComponentCount; c++)
                names[c] = arch.Types[c].Type.Name;
            return new ArchetypeMemoryInfo
            {
                TypeSignature = string.Join(", ", names),
                ChunkCount = arch.ChunkCount,
                EntityCount = arch.EntityCount,
                Capacity = arch.ChunkCount > 0 ? arch.ChunkList[0].Capacity : 0,
                SlabCount = arch.SlabCount,
                SlabBytes = arch.SlabBytes,
            };
        }

        // Prefab 实例化

        /// <summary>从 Prefab 模板复制创建 count 个实例（对齐 Unity Instantiate）：组件值独立复制，实例不带 Prefab 标记；
        /// 持有资源的组件靠复制/析构 hook 转移，整批准备完成后才发布实体与关系。</summary>
        public Entity[] SpawnFrom(Entity prefabEntity, int count)
        {
            CheckDisposed();
            if (count <= 0) return Array.Empty<Entity>();
            CompleteActiveJobs();
            using var changes = BeginRelationChanges();
            using (EnterStructural()) return CloneEntitiesCore(new[] { prefabEntity }, count, requirePrefab: true);
        }
        // 数据导航（调试 dump）

        /// <summary>打印单个实体的所有组件字段值（用组件元数据，非反射）。</summary>
        public unsafe string DumpEntity(Entity entity)
        {
            CheckDisposed();
            ref var info = ref GetEntityInfoRef(entity.Id);
            if (info.Archetype == null)
                return "<destroyed entity>";
            if (info.Version != entity.Version)
                return "<stale entity>";
            return DumpEntityInChunk(info.Archetype, info.Archetype.ChunkList[info.ChunkIndex], info.SlotInChunk, entity);
        }

        /// <summary>打印指定组件签名 Archetype 的所有实体。</summary>
        public string DumpArchetype(params ComponentType[] types)
        {
            CheckDisposed();
            Archetype? arch = null;
            for (int i = 0; i < archetypeCount; i++)
            {
                var a = allArchetypes[i];
                if (a != null && Utils.SetEquals(a.Types, types))
                {
                    arch = a;
                    break;
                }
            }
            if (arch == null)
                return $"<archetype not found: {string.Join(", ", types.Select(t => t.Type.Name))}>";

            var sb = new StringBuilder();
            sb.AppendLine($"Archetype [{string.Join(", ", arch.Types.ToArray().Select(t => t.Type.Name))}] chunk={arch.ChunkCount} 实体={arch.EntityCount}");
            foreach (var chunk in arch.ChunkList)
            {
                for (int slot = 0; slot < chunk.EntityCount; slot++)
                    sb.Append(DumpEntityInChunk(arch, chunk, slot, chunk.GetEntity(slot)));
            }
            return sb.ToString();
        }

        /// <summary>打印所有 Archetype 的概览（组件签名 + 实体数 + chunk 数 + slab）。</summary>
        public string DumpWorld()
        {
            CheckDisposed();
            var sb = new StringBuilder();
            sb.AppendLine($"World: {entityCount} 实体, archetype 数={archetypeCount}");
            for (int i = 0; i < archetypeCount; i++)
            {
                var arch = allArchetypes[i];
                if (arch == null) continue;
                string sig = string.Join(", ", arch.Types.ToArray().Select(t => t.Type.Name));
                sb.AppendLine($"  [{sig}] 实体={arch.EntityCount}, chunk={arch.ChunkCount}, slab={arch.SlabBytes / 1024}KB");
            }
            return sb.ToString();
        }

        private unsafe string DumpEntityInChunk(Archetype arch, Chunk chunk, int slot, Entity entity)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  Entity {entity.Id}:{entity.Version}");
            for (int i = 0; i < arch.ComponentCount; i++)
            {
                var type = arch.Types[i];
                var meta = ComponentMetaRegistry.Get(type.Id);
                byte* compPtr = (byte*)chunk.GetComponentArrayPointer(i) + slot * type.Size;

                if (meta.TypeName == null || meta.Fields == null || meta.Fields.Length == 0)
                {
                    sb.AppendLine($"    {type.Type.Name}: <无字段元数据>");
                    continue;
                }

                sb.Append($"    {meta.TypeName}: ");
                foreach (var f in meta.Fields)
                    sb.Append($"{f.Name}={ReadFieldValue(compPtr + f.Offset, f.Kind)} ");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static unsafe string ReadFieldValue(void* ptr, FieldKind kind)
        {
            return kind switch
            {
                FieldKind.Int8 => (*(sbyte*)ptr).ToString(),
                FieldKind.Int16 => (*(short*)ptr).ToString(),
                FieldKind.Int32 => (*(int*)ptr).ToString(),
                FieldKind.Int64 => (*(long*)ptr).ToString(),
                FieldKind.UInt8 => (*(byte*)ptr).ToString(),
                FieldKind.UInt16 => (*(ushort*)ptr).ToString(),
                FieldKind.UInt32 => (*(uint*)ptr).ToString(),
                FieldKind.UInt64 => (*(ulong*)ptr).ToString(),
                FieldKind.Float32 => (*(float*)ptr).ToString(),
                FieldKind.Float64 => (*(double*)ptr).ToString(),
                FieldKind.Bool => (*(bool*)ptr).ToString(),
                FieldKind.Char => (*(char*)ptr).ToString(),
                FieldKind.Decimal => (*(decimal*)ptr).ToString(),
                _ => "?",
            };
        }

        // World 组件值快照

        /// <summary>等待已登记作业，按组件内存布局保存支持的值状态。</summary>
        public unsafe WorldSnapshot TakeSnapshot()
        {
            CheckDisposed();
            CompleteActiveJobs();
            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms);
            w.Write(SnapshotMagic.ToCharArray());
            w.Write(IntPtr.Size);

            var archetypes = CollectSnapshotArchetypes();
            ValidateSnapshotRelationships();
            WriteSnapshotSignatures(w, archetypes);
            WriteSnapshotRows(w, archetypes);
            WriteManagedRelations(w);
            w.Flush();
            return new WorldSnapshot(ms.ToArray());
        }

        private List<Archetype> CollectSnapshotArchetypes()
        {
            var archetypes = new List<Archetype>();
            for (int i = 0; i < archetypeCount; i++)
                if (allArchetypes[i] != null && allArchetypes[i].EntityCount > 0)
                    archetypes.Add(allArchetypes[i]);
            return archetypes;
        }

        // 签名段：每个原型的组件类型 id、大小、类型名与模块版本，供恢复时校验兼容性。
        private void WriteSnapshotSignatures(System.IO.BinaryWriter w, List<Archetype> archetypes)
        {
            w.Write(archetypes.Count);
            foreach (var arch in archetypes)
            {
                w.Write(arch.ComponentCount);
                foreach (var t in arch.Types)
                {
                    ValidateSnapshotComponent(t);
                    w.Write(t.Id);
                    w.Write(t.Size);
                    w.Write(t.Type.AssemblyQualifiedName);
                    w.Write(t.Type.Module.ModuleVersionId.ToByteArray());
                }
            }
        }

        // 数据段：总实体数，随后逐实体写 id/版本/原型下标与全部组件字节。
        private unsafe void WriteSnapshotRows(System.IO.BinaryWriter w, List<Archetype> archetypes)
        {
            int totalEntities = 0;
            foreach (var arch in archetypes) totalEntities += arch.EntityCount;
            w.Write(totalEntities);

            for (int archIdx = 0; archIdx < archetypes.Count; archIdx++)
            {
                var arch = archetypes[archIdx];
                foreach (var chunk in arch.ChunkList)
                {
                    for (int slot = 0; slot < chunk.EntityCount; slot++)
                    {
                        Entity e = chunk.GetEntity(slot);
                        w.Write(e.Id);
                        w.Write(e.Version);
                        w.Write(archIdx);
                        for (int c = 0; c < arch.ComponentCount; c++)
                        {
                            var t = arch.Types[c];
                            byte* compPtr = (byte*)chunk.GetComponentArrayPointer(c) + slot * t.Size;
                            w.Write(new ReadOnlySpan<byte>(compPtr, t.Size));
                        }
                    }
                }
            }
        }

        /// <summary>完整校验后恢复组件值，拒绝不兼容快照时保留当前 World。</summary>
        public void Restore(WorldSnapshot snapshot) => RestorePreparedSnapshot(snapshot);

        /// <summary>
        /// 设置组件值（泛型版本，调用 SetRaw）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(Entity entity, T t) where T : struct, IComponentData
        {
            SetRaw(entity, typeof(T), t);
        }

        /// <summary>
        /// 设置组件值（非泛型版本，核心实现）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void SetRaw(Entity entity, Type componentType, object value)
        {
            CheckDisposed();
            if (ComponentTypeManager.GetComponentType(componentType).IsRelation)
            {
                ValidateEntity(entity);
                if (!GetEntityInfoRef(entity.Id).Archetype.Has(componentType))
                    throw new InvalidOperationException($"Entity {entity} has no '{componentType}' column.");
                AddComponentRaw(entity, componentType, value);
                return;
            }
            CompleteEntityJobs(entity);
            using (EnterStructural())
            {
                ref var entityInfoRef = ref GetAliveInfoRef(entity);
                var arch = entityInfoRef.Archetype;
                arch.SetRaw(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk, componentType, value);

                // Observer：Set（写入后派发，NewValue 用写入值；v1 不提供 OldValue；count=1）
                if (_observerCount > 0 && _observers != null)
                {
                    var compTypeForObs = ComponentTypeManager.GetComponentType(componentType);
                    if (_observers.TryGetValue(compTypeForObs.Id, out var reg) && reg.Set.Count > 0)
                    {
                        var valueHandle = GCHandle.Alloc(value, GCHandleType.Pinned);
                        try
                        {
                            // Set 桶复用 DispatchAdded（新值语义）
                            DispatchAdded(reg.Set, &entity, (void*)valueHandle.AddrOfPinnedObject(), 1);
                        }
                        finally
                        {
                            valueHandle.Free();
                        }
                    }
                }
            }
        }

        /// <summary>读取组件值引用（OOD 稀疏访问面）：ref 在下次结构变更前有效，与 <see cref="EntityIndexInWorld"/> 同一纪律。
        /// 读路径 lock-free 依赖"结构性 API 先 CompleteActiveJobs 且 job 执行中抛异常"，多线程读取可定义
        /// ENTJOY_SAFE_ENTITY_READS 恢复锁。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(Entity entity) where T : struct, IComponentData
        {
            CheckDisposed();
#if ENTJOY_SAFE_ENTITY_READS
            using (EnterStructural())
#endif
            {
                ref var entityInfoRef = ref GetAliveInfoRef(entity);
                var arch = entityInfoRef.Archetype;
                return ref arch.GetComponent<T>(entityInfoRef.ChunkIndex, entityInfoRef.SlotInChunk);
            }
        }

        /// <summary>
        /// 创建稀疏随机访问句柄（对齐 Unity ComponentLookup）。普通 struct，可作系统字段；
        /// main-thread only，持有本 EntityManager 强引用。
        /// </summary>
        public unsafe ComponentLookup<T> GetComponentLookup<T>() where T : struct
            => new ComponentLookup<T>(this);

        public ReadOnlyComponentLookup<T> GetReadOnlyComponentLookup<T>() where T : struct
            => new ReadOnlyComponentLookup<T>(this);

        /// <summary>读取当前组件值副本，不暴露关系列的写入引用。</summary>
        public T GetComponentReadOnly<T>(Entity entity) where T : struct
        {
            CheckDisposed();
            ValidateEntity(entity);
            ref var info = ref GetEntityInfoRef(entity.Id);
            return info.Archetype.ChunkList[info.ChunkIndex]
                .GetComponentReadOnly<T>(info.SlotInChunk, info.Archetype.GetComponentTypeIndex<T>());
        }
    }

    public unsafe partial class EntityManager
    {
        /// <summary>设置 enableable 组件的启用状态；实体不含该组件或组件不可 enable 时抛出。</summary>
#pragma warning disable CS0618 // 保留旧接口兼容
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetComponentEnabled<T>(Entity entity, bool enabled) where T : struct, IEnableableComponent
        {
            CheckDisposed();
            CompleteEntityJobs(entity);
            using (EnterStructural())
            {
                ref var info = ref GetAliveInfoRef(entity);
                int componentIndex = RequireComponentIndex<T>(info.Archetype, entity);
                info.Archetype.ChunkList[info.ChunkIndex].SetComponentEnabled(componentIndex, info.SlotInChunk, enabled);
            }
        }

        /// <summary>读取 enableable 组件的启用状态；实体不含该组件或组件不可 enable 时抛出。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsComponentEnabled<T>(Entity entity) where T : struct, IEnableableComponent
        {
            CheckDisposed();
#if ENTJOY_SAFE_ENTITY_READS
            using (EnterStructural())
#endif
            {
                ref var info = ref GetAliveInfoRef(entity);
                int componentIndex = RequireComponentIndex<T>(info.Archetype, entity);
                return info.Archetype.ChunkList[info.ChunkIndex].GetComponentEnabled(componentIndex, info.SlotInChunk);
            }
        }

        /// <summary>取组件在该实体所属原型中的列下标；实体没有该组件时抛出。</summary>
        private static int RequireComponentIndex<T>(Archetype archetype, Entity entity) where T : struct
        {
            if (!archetype.Has(typeof(T)))
                throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");
            return archetype.GetComponentTypeIndex<T>();
        }
    }
}
