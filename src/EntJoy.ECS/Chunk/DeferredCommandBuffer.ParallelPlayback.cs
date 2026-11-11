using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    public unsafe partial class DeferredCommandBuffer
    {
        private EcbWriterState[] _writerPlaybackBatch = Array.Empty<EcbWriterState>();
        private readonly struct ParallelCommand
        {
            public readonly int Writer, Offset, SortKey;
            public ParallelCommand(int writer, int offset, int sortKey) { Writer = writer; Offset = offset; SortKey = sortKey; }
        }

        private static int NextWriterCommand(ref EcbWriterState state, int offset)
        {
            int opcode = *(int*)(state.Staging + offset);
            return opcode switch
            {
                OP_DESTROY_RANGE => checked(offset + sizeof(int) * 3),
                OP_SET_COMPONENT_SINGLE => checked(offset + sizeof(int) * 3 + sizeof(Entity)
                    + *(int*)(state.Staging + offset + sizeof(int) * 2 + sizeof(Entity))),
                >= OP_SET_RELATIONSHIP_TARGET and <= OP_CLEAR_MULTI_RELATIONSHIPS => checked(offset + RelationshipCommandSize),
                _ => throw new InvalidOperationException($"Unsupported ParallelWriter opcode {opcode}.")
            };
        }

        private static int WriterCommandCount(EcbWriterState[] batch)
        {
            int count = 0;
            for (int writer = 0; writer < batch.Length; writer++)
                for (int offset = 0; offset < batch[writer].Offset; offset = NextWriterCommand(ref batch[writer], offset)) count++;
            return count;
        }

        private static List<ParallelCommand> SortWriterCommands(EcbWriterState[] batch)
        {
            var commands = new List<ParallelCommand>();
            for (int writer = 0; writer < batch.Length; writer++)
                for (int offset = 0; offset < batch[writer].Offset; offset = NextWriterCommand(ref batch[writer], offset))
                {
                    int opcode = *(int*)(batch[writer].Staging + offset);
                    int key = IsRelationshipOpcode(opcode)
                        ? *(int*)(batch[writer].Staging + offset + sizeof(int)) : 0;
                    commands.Add(new ParallelCommand(writer, offset, key));
                }
            commands.Sort(static (a, b) =>
            {
                int order = a.SortKey.CompareTo(b.SortKey);
                if (order == 0) order = a.Writer.CompareTo(b.Writer);
                return order != 0 ? order : a.Offset.CompareTo(b.Offset);
            });
            return commands;
        }

        private static void WriteInlineComponent(EntityManager manager, byte* command)
        {
            var entity = *(Entity*)(command + sizeof(int));
            int typeId = *(int*)(command + sizeof(int) + sizeof(Entity));
            int size = *(int*)(command + sizeof(int) * 2 + sizeof(Entity));
            byte* payload = command + sizeof(int) * 3 + sizeof(Entity);
            if (new ComponentType(typeId).IsRelation) manager.WriteComponentRange(typeId, size, &entity, payload, 1);
            else if (manager.TryGetComponentPointer(entity, typeId, size, out void* target)) Buffer.MemoryCopy(payload, target, size, size);
        }

        private static void ReplayParallelCommand(EntityManager manager, ref EcbWriterState state, int offset)
        {
            byte* command = state.Staging + offset;
            switch (*(int*)command)
            {
                case OP_DESTROY_RANGE:
                    manager.DestroyEntities(state.Destroys + *(int*)(command + 4), *(int*)(command + 8));
                    break;
                case OP_SET_COMPONENT_SINGLE: WriteInlineComponent(manager, command); break;
                case OP_SET_RELATIONSHIP_TARGET:
                case OP_REMOVE_RELATIONSHIP:
                case OP_ADD_MULTI_RELATIONSHIP:
                case OP_REMOVE_MULTI_RELATIONSHIP:
                case OP_CLEAR_MULTI_RELATIONSHIPS: ReplayRelationship(manager, command); break;
                default: throw new InvalidOperationException("Unsupported ParallelWriter command.");
            }
        }

        private void ReturnWriterBuffers(EcbWriterState[] batch)
        {
            for (int i = 0; i < batch.Length; i++)
            {
                ref var next = ref _writers[i];
                if (next.Offset == 0)
                {
                    if (next.Staging != null) Marshal.FreeHGlobal((IntPtr)next.Staging);
                    next.Staging = batch[i].Staging;
                    next.Capacity = batch[i].Capacity;
                    next.OwnerThread = 0;
                }
                else if (batch[i].Staging != null) Marshal.FreeHGlobal((IntPtr)batch[i].Staging);
                if (next.DestroyCount == 0)
                {
                    if (next.Destroys != null) Marshal.FreeHGlobal((IntPtr)next.Destroys);
                    next.Destroys = batch[i].Destroys;
                    next.DestroyCapacity = batch[i].DestroyCapacity;
                }
                else if (batch[i].Destroys != null) Marshal.FreeHGlobal((IntPtr)batch[i].Destroys);
            }
        }

        // 把各 writer 的缓冲摘进本批快照，同时清空 writer 供下一轮复用；返回是否需要按关系键排序。
        private bool PrepareWriterBatch(EcbWriterState[] batch)
        {
            bool ordered = false;
            for (int i = 0; i < batch.Length; i++)
            {
                batch[i] = _writers[i];
                ordered |= batch[i].HasRelationshipCommands;
                _writers[i] = new EcbWriterState { Capacity = batch[i].Capacity, DestroyCapacity = batch[i].DestroyCapacity, Shared = batch[i].Shared };
            }
            return ordered;
        }

        private void PlaybackWriterBatch(EntityManager manager)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playingBack) throw new InvalidOperationException("ECB playback cannot be nested.");
            if (_writerPlaybackBatch.Length != _writerCount) _writerPlaybackBatch = new EcbWriterState[_writerCount];
            var batch = _writerPlaybackBatch;
            bool ordered = PrepareWriterBatch(batch);
            _playingBack = true;
            int completed = 0, writerIndex = -1;
            List<ParallelCommand> commands = null;
            try
            {
                if (ordered)
                {
                    commands = SortWriterCommands(batch);
                    foreach (var command in commands)
                    {
                        writerIndex = command.Writer;
                        ReplayParallelCommand(manager, ref batch[command.Writer], command.Offset);
                        completed++;
                    }
                }
                else
                    for (int i = 0; i < batch.Length; i++)
                    {
                        writerIndex = i;
                        if (batch[i].Offset > 0) ReplayWriterBuffer(manager, ref batch[i], ref completed);
                    }
            }
            catch (Exception cause)
            {
                int total = commands?.Count ?? WriterCommandCount(batch);
                throw new CommandPlaybackException(completed, Math.Max(0, total - completed - 1), writerIndex, cause);
            }
            finally { ReturnWriterBuffers(batch); Array.Clear(batch); _playingBack = false; }
        }
    }
}
