#include "NativeContainers.h"
#include <cstdio>
#include <stdexcept>
#include <vector>

template<class T>
void CheckColumns()
{
    for (int capacity : {1, 2, 3, 4, 128, 192, 256})
    {
        std::vector<T> first(capacity), second(capacity), last(capacity);
        T* chunks[] = {first.data(), second.data(), last.data()};
        int shift = -1;
        if ((capacity & (capacity - 1)) == 0)
        {
            shift = 0;
            while ((1 << shift) < capacity) ++shift;
        }
        EntJoy::Collections::NativeColumn<T> column{
            nullptr, chunks, capacity, shift, capacity * 3 - 1};
        for (int index = 0; index < column.Length; ++index)
        {
            T* expected = chunks[index / capacity] + index % capacity;
            if (column.GetElementPtr(index) != expected || &column[index] != expected)
                throw std::runtime_error("chunked column address mismatch");
            column[index] = T{};
        }
        EntJoy::Collections::NativeColumn<T> flat{first.data(), nullptr, 0, 0, capacity};
        for (int index = 0; index < capacity; ++index)
            if (flat.GetElementPtr(index) != first.data() + index)
                throw std::runtime_error("flat column address mismatch");
    }
}

struct alignas(32) AlignedValue { int value; };

int main()
{
    static_assert(sizeof(EntJoy::Collections::NativeColumn<int>) == 32);
    try
    {
        CheckColumns<int>();
        CheckColumns<int64_t>();
        CheckColumns<AlignedValue>();
        std::puts("PASS NativeColumn pointer, stride, tail and ABI contracts");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::fprintf(stderr, "%s\n", error.what());
        return 1;
    }
}
