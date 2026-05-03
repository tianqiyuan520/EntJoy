// NativeContainers.h
#pragma once
#include <cstddef>
#include <cstdint>
#include <cstdlib>

namespace EntJoy {
	namespace Collections {

		// ---------- 分配器枚举（必须与 C# 侧 Allocator 一致） ----------
		enum Allocator {
			Invalid = 0,
			None = 1,
			Temp = 2,
			TempJob = 3,
			Persistent = 4,
		};

		// ---------- 原子安全句柄（布局与 C# AtomicSafetyHandle 一致） ----------
		struct AtomicSafetyHandle {
			intptr_t handle;
		};

		// ---------- 清理选项 ----------
		enum NativeArrayOptions {
			ClearMemory = 0,
			UninitializedMemory = 1
		};

		// ========== NativeArray ==========
		// 内存布局与 C# 侧 EntJoy.Collections.NativeArray<T> 完全一致
		template<typename T>
		struct NativeArray {
			void* m_Buffer;               // 对应 C# 的 void* _buffer
			int32_t m_Length;             // 对应 int _length
			Allocator m_Allocator;        // 对应 Allocator _allocator
			AtomicSafetyHandle m_Safety;  // 对应 AtomicSafetyHandle _safety
			bool m_IsOwner;               // 对应 bool _isOwner
			// 调试字段忽略

			T* data() const { return static_cast<T*>(m_Buffer); }
			T* GetUnsafePtr() const { return static_cast<T*>(m_Buffer); }
			int32_t length() const { return m_Length; }

			T& operator[](int32_t index) { return data()[index]; }
			const T& operator[](int32_t index) const { return data()[index]; }
		};

		// ========== UnsafeList ==========
		// 非托管堆上的实际列表数据，C# 侧 NativeList<T> 持有 UnsafeList<T>* _listData
		template<typename T>
		struct UnsafeList {
			T* Ptr;                       // 数据缓冲区指针
			int32_t Length;               // 当前元素个数
			int32_t Capacity;             // 容量
			Allocator Allocator;          // 分配器类型

			// 辅助方法
			T* data() const { return Ptr; }
			int32_t length() const { return Length; }
			int32_t capacity() const { return Capacity; }
			T* GetUnsafePtr() const { return Ptr; }

			T& operator[](int32_t index) { return Ptr[index]; }
			const T& operator[](int32_t index) const { return Ptr[index]; }

			// 扩容方法，与 C# 侧 UnsafeList<T>.Resize 行为一致
			void Resize(int32_t newSize, NativeArrayOptions options = NativeArrayOptions::ClearMemory) {
				if (newSize < 0) return;
				if (newSize > Capacity) {
					EnsureCapacity(newSize);
				}
				if (newSize > Length && options == NativeArrayOptions::ClearMemory) {
					size_t clearBytes = static_cast<size_t>(newSize - Length) * sizeof(T);
					uint8_t* start = reinterpret_cast<uint8_t*>(Ptr + Length);
					for (size_t i = 0; i < clearBytes; ++i) start[i] = 0;
				}
				Length = newSize;
			}

			// 确保容量
			void EnsureCapacity(int32_t minCapacity) {
				if (Capacity >= minCapacity) return;
				int32_t newCapacity = (Capacity == 0) ? 4 : Capacity * 2;
				if (newCapacity < minCapacity) newCapacity = minCapacity;
				T* newPtr = static_cast<T*>(malloc(newCapacity * sizeof(T)));
				if (Ptr) {
					for (int32_t i = 0; i < Length; ++i) {
						newPtr[i] = Ptr[i];
					}
					free(Ptr);
				}
				Ptr = newPtr;
				Capacity = newCapacity;
			}

			// 添加元素（内部调用 EnsureCapacity）
			void Add(const T& value) {
				EnsureCapacity(Length + 1);
				Ptr[Length++] = value;
			}

			// 释放内部缓冲区
			void Dispose() {
				if (Ptr) {
					free(Ptr);
					Ptr = nullptr;
					Length = 0;
					Capacity = 0;
				}
			}
		};

		// ========== NativeList ==========
		// 与 C# 侧 NativeList<T> 布局一致，包装 UnsafeList<T>*
		template<typename T>
		struct NativeList {
			UnsafeList<T>* m_ListData;    // 指向非托管堆上的 UnsafeList
			Allocator m_Allocator;
			AtomicSafetyHandle m_Safety;
			bool m_IsOwner;

			// 便捷访问（直接操作 UnsafeList）
			UnsafeList<T>* operator->() const { return m_ListData; }
			UnsafeList<T>& operator*() const { return *m_ListData; }

			T* data() const { return m_ListData ? m_ListData->Ptr : nullptr; }
			T* GetUnsafePtr() const { return m_ListData ? m_ListData->Ptr : nullptr; }

			int32_t length() const { return m_ListData ? m_ListData->Length : 0; }
			int32_t capacity() const { return m_ListData ? m_ListData->Capacity : 0; }

			T& operator[](int32_t index) { return (*m_ListData)[index]; }
			const T& operator[](int32_t index) const { return (*m_ListData)[index]; }

			// 直接代理 UnsafeList 的方法
			void Resize(int32_t newSize, NativeArrayOptions options = NativeArrayOptions::ClearMemory) {
				if (m_ListData) m_ListData->Resize(newSize, options);
			}

			void Add(const T& value) {
				if (m_ListData) m_ListData->Add(value);
			}

			void EnsureCapacity(int32_t minCapacity) {
				if (m_ListData) m_ListData->EnsureCapacity(minCapacity);
			}

			// 注意：在 Job 场景中，通常直接传递 m_ListData 指针，因此 C++ 函数应接收 UnsafeList<T>*
		};

	} // namespace Collections
} // namespace EntJoy

// 全局别名（便于使用）
using EntJoy::Collections::NativeArray;
using EntJoy::Collections::NativeList;
using EntJoy::Collections::UnsafeList;
using EntJoy::Collections::Allocator;
using EntJoy::Collections::AtomicSafetyHandle;
using EntJoy::Collections::NativeArrayOptions;