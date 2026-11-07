using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    public sealed class CommandPlaybackException : InvalidOperationException
    {
        public int CommandIndex { get; }
        public int UnexecutedCommandCount { get; }
        public int WriterIndex { get; }
        internal CommandPlaybackException(int index, int remaining, int writer, Exception cause)
            : base($"ECB command {index} failed (writer {writer}); {remaining} following commands were not executed. The recorded batch has been consumed.", cause)
        { CommandIndex = index; UnexecutedCommandCount = remaining; WriterIndex = writer; }
    }

    public unsafe partial class DeferredCommandBuffer
    {
        internal const int OP_SET_RELATIONSHIP_TARGET = 7;
        internal const int OP_REMOVE_RELATIONSHIP = 8;
        internal static readonly int RelationshipCommandSize = sizeof(int) * 3 + sizeof(Entity) * 2;
        private bool _playingBack;
        private List<ComponentType[]> _spareTypeSets = new();

        internal static int SingleRelationTypeId<TRel>() where TRel : struct, IRelationComponent
        {
            var type = ComponentTypeManager.GetComponentType(typeof(TRel));
            if (type.IsMultiRelation) throw new NotSupportedException("Target-set and unlink commands require a single-target relationship.");
            return type.Id;
        }

        /// <summary>回放时重绑目标并保留已有业务字段；记录阶段不读取关系图。</summary>
        public void SetRelationshipTarget<TRel>(Entity source, Entity target) where TRel : struct, IRelationComponent
            => RecordRelationship(OP_SET_RELATIONSHIP_TARGET, SingleRelationTypeId<TRel>(), source, target);

        public void RemoveRelationship<TRel>(Entity source) where TRel : struct, IRelationComponent
            => RecordRelationship(OP_REMOVE_RELATIONSHIP, SingleRelationTypeId<TRel>(), source, default);

        private void RecordRelationship(int opcode, int typeId, Entity source, Entity target)
        {
            lock (_sync)
            {
                EnsureCapacity(RelationshipCommandSize);
                WriteRelationship(_staging + _stagingOffset, opcode, 0, typeId, source, target);
                _stagingOffset += RelationshipCommandSize;
                CloseDestroyRun();
                CommandCount++;
            }
        }

        internal static void WriteRelationship(byte* command, int opcode, int sortKey, int typeId, Entity source, Entity target)
        {
            *(int*)command = opcode;
            *(int*)(command + 4) = sortKey;
            *(int*)(command + 8) = typeId;
            *(Entity*)(command + 12) = source;
            *(Entity*)(command + 12 + sizeof(Entity)) = target;
        }

        private static void ReplayRelationship(EntityManager manager, byte* command)
        {
            var source = *(Entity*)(command + 12);
            if (!manager.IsRelationshipTargetAlive(RelationSlot.From(source))) return;
            int typeId = *(int*)(command + 8);
            var type = ComponentTypeManager.GetComponentType(ComponentTypeManager.GetTypeByComponentType(typeId));
            if (*(int*)command == OP_SET_RELATIONSHIP_TARGET)
                manager.SetRelationshipTargetRaw(source, *(Entity*)(command + 12 + sizeof(Entity)), type);
            else manager.RemoveRelationshipRaw(source, type);
        }
    }
}
