# NativeAllocator Unity 对齐分析与计划

> 本文档记录分配器设计分析与改造计划。当前实现见 [01-NativeAllocator-实现说明](./01-NativeAllocator-实现说明.md)。
> **状态：计划阶段，尚未实施。** 改造范围 = Level 1（块基址契约）+ Level 2（按标签分配器函数表）。

---

## 1. 为什么对齐 Unity

上一轮用托管回调把原生 `free` 挡回 `PersistentAllocator.Free`，修好了 0xc0000374。但**契约仍违背 Unity 模型**：

- 我们 `PersistentAllocator.Alloc` 返回 `payload = base+16`，把 header（classIndex/payloadSize）写在**返回指针前面**。
- 任何释放路径只要不读这个 header（例如原生 `free(Ptr)`、或未来任何拿 payload 指针做块算术的代码），就不知道块归属 → **内部指针释放 bug 类别**。

回调是"绕开"它（把所有释放路径引回一个会读 header 的入口），不是消灭它。Unity 从结构上消灭——分配器返回的是**可释放块基址**，元数据放 side-table，释放按容器携带的标签走唯一入口。

---

## 2. Unity NativeCollection 四层设计对照

| # | Unity 特性 | 我们现状 | 差距 |
|---|-----------|---------|------|
| ① | **分配器返回可释放块基址**；元数据在 side-table（slab 块算术 / 分配器自有映射），不在返回指针前面 | payload = base+16，header 写在返回指针前，free 读 `*(int*)(payload-16)` | **有**（Level 1 待做） |
| ② | **分配器标签随身带**，`Dispose` 用 `Allocator` 标签查函数表释放（free-by-label） | C# 容器与 native `UnsafeList` 都带 `Allocator` 字段，但 native 扩容/释放只走 Persistent 回调对，**忽略自身标签** | **有**（Level 2 待做） |
| ③ | 原生（Burst/IL2CPP）走同一 allocator 函数指针表，无独立 malloc 回路 | `PersistentAllocFn/Fre` 回调 + 未注册回退 malloc/free | ✅ 已达成（单分配器特化） |
| ④ | Temp = 主线程 rewindable 栈；TempJob = ring-buffer/缓存，瞬态地址稳定 | `TempAllocator` 裸 `AllocHGlobal` + 字典 + 帧末全量释放，零复用 | **有**（Level 3，暂缓） |

### 2.1 补充说明

- **free-by-label 的本质**：Unity 容器头存 `m_AllocatorLabel`，`Resize`/`Dispose` 的 free 都用同一个 label 查表 → "谁分配、谁释放"永远成立，不存在第二个未注册的 free 路径。我们的回调方案就是"label 随身带"的**单分配器特化版**。
- **Unity 不返回内部指针**：分配器返回的指针就是它追踪的块基址。元数据恢复靠 slab 块算术（指针向下取整到块边界 + 索引）或分配器自有映射，不读用户内存前面的字节。
- **resize 本身两边一致**（新块 + memcpy + free 旧块）；差别在分配器契约，不在 resize 操作。
- **Unity 不用 ISPC**：DOTS 用 Burst（LLVM），allocator 只对 C# 容器 + Burst 原生两条路径，都经 label 驱动，无第三类"ISPC 侧分配"。

---

## 3. Level 1 计划：块基址契约（PersistentAllocator v2）

改 `src/EntJoy/Collections/PersistentAllocator.cs`：

- `s_live`：`ConcurrentDictionary<IntPtr, byte>` → `ConcurrentDictionary<IntPtr, int>`，**以返回的 payload 指针为 key**，值为 classIndex（0..30 可池化；-1 = 直通 OS）。
- 删 `WriteHeader` 与 payloadSize 概念；`HeaderSize=16` 保留为**对齐垫**（只垫字节，不写内存）。
- `Alloc(size)`：

  ```
  size<=0 → 1; idx = SizeToClass(size)
  oversize(idx>30): base=AllocHGlobal(size+16); s_live[base+16]=-1; return base+16
  pool hit:          s_live[ptr+16]=idx;                          return ptr+16
  pool miss: base=AllocHGlobal((1<<idx)+16); s_live[base+16]=idx; return base+16
  ```

- `Free(payload)`：

  ```
  if (!s_live.TryRemove(payload, out idx)) → 外来块：FreeHGlobal(payload)（保持现状）
  else if (idx<0)                          → oversize：FreeHGlobal(payload-16)
  else if (class.Count >= MaxPerClass)     → FreeHGlobal(payload-16); toOS++
  else                                     → class.Push(payload-16)
  ```

- **不再 `*(int*)(payload-16)` 读头**。返回指针 = 分配器唯一追踪对象；元数据查表。
- 统计形状不变（Allocs/Frees/Hits/Misses/ToOS/Foreign）。

**为什么根治**：`payload-16` 仍用于 FreeHGlobal（对齐垫），但元数据恢复不再依赖读用户内存——任何释放路径最终都走 `PersistentAllocator.Free`，而它只查 side-table。内部指针 bug 类别消失。

---

## 4. Level 2 计划：按标签分配器函数表（Unity AllocatorManager 精简版）

### 4.1 `src/NativeDll/NativeContainers.h`

替换单回调对为按标签表：

```cpp
using AllocCallback = void* (*)(int32_t size);
using FreeCallback  = void  (*)(void* ptr);

inline AllocCallback& AllocFnFor(Allocator label) {
    static AllocCallback table[8] = {};              // label 0..7；0=Invalid 槽恒 null
    int i = static_cast<int>(label);
    return (i >= 0 && i < 8) ? table[i] : table[0];  // 越界→Invalid 槽（回退 malloc/free）
}
inline FreeCallback&  FreeFnFor(Allocator label) { /* 同构 */ }
inline void RegisterAllocator(Allocator label, AllocCallback alloc, FreeCallback free) {
    AllocFnFor(label) = alloc; FreeFnFor(label) = free;
}
```

- `UnsafeList<T>::EnsureCapacity` / `Dispose`：改用 `AllocFnFor(Allocator)` / `FreeFnFor(Allocator)`（**自身标签**），null 时回退 malloc/free。
- `Allocator` 枚举值（Invalid=0/None=1/Temp=2/TempJob=3/Persistent=4）已与 C# 镜像，保持同步。
- 表 init-only（Initialize 期单线程写，job Submit 前完成；worker 经 job 提交的 release/acquire 可见）。**注册必须先于任何 Submit**。

### 4.2 `src/NativeDll/Exports.h` / `Exports.cpp`

```cpp
typedef void* (*AllocatorAllocFn)(int32_t size);
typedef void  (*AllocatorFreeFn)(void* ptr);
JOB_API void JobSystem_RegisterAllocator(int32_t label, AllocatorAllocFn alloc, AllocatorFreeFn free);
// cpp: EntJoy::Collections::RegisterAllocator(static_cast<Allocator>(label), alloc, free);
```

替换现有 `JobSystem_RegisterPersistentAllocator`。

### 4.3 `src/EntJoy/JobSystem/NativeJobScheduler.cs`

- 字段 + GetExport：`JobSystem_RegisterPersistentAllocator` → `JobSystem_RegisterAllocator`（签名加 `int` 标签参数）。
- 注册：`_jobSystem_RegisterAllocator((int)EntJoy.Collections.Allocator.Persistent, &PersistentAllocUnmanaged, &PersistentFreeUnmanaged);`（`[UnmanagedCallersOnly]` 包装保留）。

### 4.4 改动文件

| 文件 | 改动 |
|------|------|
| `src/EntJoy/Collections/PersistentAllocator.cs` | L1：side-table 元数据、无 header |
| `src/NativeDll/NativeContainers.h` | L2：按标签表 + UnsafeList 按自身标签释放 |
| `src/NativeDll/Exports.h` / `Exports.cpp` | L2：`JobSystem_RegisterAllocator(label,...)` |
| `src/EntJoy/JobSystem/NativeJobScheduler.cs` | L2：按标签注册 |

---

## 5. 暂缓项与护栏

### 5.1 暂缓（Level 3+）

- **TempJob free-list 缓存**：实测 QueryWall−QueryCore 差距仅 ~0.02ms，非方差主源；但 Unity 第 ④ 条属性（瞬态分配器复用）最终应补齐。
- **slab + 块算术 persistent 分配器**：side-table 已达成同一用户契约，无必要重写。
- **32 字节对齐升级**（ISPC AVX2）：独立 perf 后续项，当前 16 字节无回归。

### 5.2 护栏

- 外来块 OS 释放语义不变（FreeHGlobal 回退保留）。
- 不碰 JobSystem 调度 / `Complete()` 自旋（2048+256 保持；200µs/500µs/4096 spin 均已被拒）。
- 不碰生成的 C++/ISPC 代码（`7c0b271` 禁区）。
- 分配器注册必须先于任何 job Submit（表 init-only）。

---

## 6. 验证方案

1. `dotnet build -c Release src/EntJoySample/EntJoySample.csproj`（native 头改动 → content-hash 触发 DLL 重建）。
2. 运行：
   ```powershell
   $env:ENTJOY_PERSISTENT_POOL_STATS="1"; dotnet run -c Release --no-build --project src/EntJoySample/EntJoySample.csproj
   ```
3. 断言：
   - `PERSISTENT_POOL|` 行：**foreign=0**（证明 side-table + 标签表路径干净）、hitRate 高、toOS 小。
   - `查询结果前10个` 不变（74945 21160 15114 75587 37949 80702 88467 19643 11454 87386）。
   - QueryCore p50 ~0.58、COLD BuildCore p50 ~0.51-0.54 —— 对已提交状态（799fe7a）无回归。
   - `DIAG|` 行完整（queryBatch=256、parkWake 等）。
4. 回归：GridSearch 正确性 + 任一活动 sample 抽查。
