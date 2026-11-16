// Flecs / EnTT 对照臂。与 tools/EcsComparison 的 .NET 侧共用 kernel、实体数、
// 测量协议与输出格式（MATRIX_RESULT 行），由 Run-NativeMatrix.ps1 交错运行后合并。
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <algorithm>
#include <string>
#include <vector>
#include "flecs.h"
#include <entt/entt.hpp>

struct A { int Value; };
struct B { int Value; };
struct C { int Value; };

// 编译器与优化等级进结果行：同一框架换编译器/优化级别会产生不同读数，不能让它们在 CSV 里混成一组。
#ifdef __clang__
#define ENTJOY_COMPILER_NAME "clang"
#define ENTJOY_COMPILER_VERSION __clang_version__
#else
#define ENTJOY_COMPILER_NAME "gcc"
#define ENTJOY_COMPILER_VERSION __VERSION__
#endif

#ifndef ENTJOY_OPT_NUMBER
#define ENTJOY_OPT_NUMBER 0
#endif

static constexpr int kSamples = 12;
static constexpr int kWarmup = 512;
static constexpr int kRepetitions = 1024;
static constexpr int kRounds = 2;

struct Timing
{
    double values[kSamples];
    double mean;
    double median;
    double minValue;
    double maxValue;
};

// 与 ComparisonTiming.Measure 同协议：warmup 后取 12 个 sample，每个 sample 是
// repetitions 次调用的总时长除以 repetitions；median 取排序后第 5、6 项的均值。
template <typename Run>
static Timing measure(Run&& run)
{
    for (int i = 0; i < kWarmup; ++i) run();

    Timing timing{};
    for (int sample = 0; sample < kSamples; ++sample)
    {
        auto start = std::chrono::steady_clock::now();
        for (int i = 0; i < kRepetitions; ++i) run();
        auto end = std::chrono::steady_clock::now();
        timing.values[sample] =
            std::chrono::duration<double, std::micro>(end - start).count() / kRepetitions;
    }

    double sum = 0;
    for (double value : timing.values) sum += value;
    timing.mean = sum / kSamples;
    std::sort(timing.values, timing.values + kSamples);
    timing.median = (timing.values[5] + timing.values[6]) / 2;
    timing.minValue = timing.values[0];
    timing.maxValue = timing.values[kSamples - 1];
    return timing;
}

static void print_result(
    int round, const char* framework, const char* layer, const char* api, const char* backend,
    const char* kernel, int entities, int components, int chunks, const Timing& timing)
{
    std::printf(
        "MATRIX_RESULT round=%d framework=%s layer=%s api=%s backend=%s kernel=%s compiler=%s "
        "opt=-O%d entities=%d "
        "components=%d chunks=%d warmup=%d bytes_per_call=0.000 verified=true "
        "mean_us=%.3f median_us=%.3f min_us=%.3f max_us=%.3f samples=%d repetitions=%d values_us=",
        round, framework, layer, api, backend, kernel, ENTJOY_COMPILER_NAME, ENTJOY_OPT_NUMBER,
        entities, components, chunks, kWarmup,
        timing.mean, timing.median, timing.minValue, timing.maxValue, kSamples, kRepetitions);
    for (int i = 0; i < kSamples; ++i)
        std::printf("%s%.3f", i ? "," : "", timing.values[i]);
    std::printf("\n");
}

// Flecs 的原生遍历形态是 table 级：iter + 组件指针 + count，没有逐实体回调 API。
// 组件 id 由 ECS_COMPONENT 宏在调用点声明，宏不能放进 if constexpr，故按组件数拆薄包装。
static void run_flecs_chunk_impl(int round, int entities, int components, ecs_world_t* world,
    ecs_entity_t idA, ecs_entity_t idB, ecs_entity_t idC)
{
    A a{0};
    B b{1};
    C c{2};
    for (int i = 0; i < entities; ++i)
    {
        ecs_entity_t entity = ecs_new(world);
        ecs_set_id(world, entity, idA, sizeof(A), &a);
        ecs_set_id(world, entity, idB, sizeof(B), &b);
        if (components == 3) ecs_set_id(world, entity, idC, sizeof(C), &c);
    }

    ecs_query_desc_t desc = {};
    desc.terms[0].id = idA;
    desc.terms[1].id = idB;
    if (components == 3) desc.terms[2].id = idC;
    ecs_query_t* query = ecs_query_init(world, &desc);
    if (!query)
    {
        std::printf("MATRIX_ERROR framework=Flecs entities=%d components=%d reason=query_init\n",
            entities, components);
        return;
    }

    auto walk = [&]()
    {
        ecs_iter_t iter = ecs_query_iter(world, query);
        while (ecs_query_next(&iter))
        {
            A* aPtr = static_cast<A*>(ecs_field_w_size(&iter, sizeof(A), 0));
            const B* bPtr = static_cast<const B*>(ecs_field_w_size(&iter, sizeof(B), 1));
            if (components == 2)
            {
                for (int i = iter.count - 1; i >= 0; --i) aPtr[i].Value += bPtr[i].Value;
            }
            else
            {
                const C* cPtr = static_cast<const C*>(ecs_field_w_size(&iter, sizeof(C), 2));
                for (int i = iter.count - 1; i >= 0; --i)
                    aPtr[i].Value += bPtr[i].Value + cPtr[i].Value;
            }
        }
    };

    // 首次遍历同时校验实体数与累加值；之后不再逐次校验，避免有符号溢出。
    int visited = 0;
    const int expected = components == 2 ? 1 : 3;
    walk();
    {
        ecs_iter_t iter = ecs_query_iter(world, query);
        while (ecs_query_next(&iter)) visited += iter.count;
    }

    bool ok = visited == entities;
    if (ok)
    {
        ecs_iter_t iter = ecs_query_iter(world, query);
        while (ecs_query_next(&iter))
        {
            const A* aPtr = static_cast<const A*>(ecs_field_w_size(&iter, sizeof(A), 0));
            for (int i = 0; i < iter.count; ++i)
                if (aPtr[i].Value != expected) ok = false;
        }
    }

    int chunks = 0;
    {
        ecs_iter_t iter = ecs_query_iter(world, query);
        while (ecs_query_next(&iter)) ++chunks;
    }

    if (!ok)
    {
        std::printf("MATRIX_ERROR framework=Flecs entities=%d components=%d visited=%d\n", entities,
            components, visited);
        ecs_query_fini(query);
        return;
    }

    Timing timing = measure(walk);
    print_result(round, "Flecs", "Chunk", "iter-span", "Cpp", "Compiler", entities, components,
        chunks, timing);

    ecs_query_fini(query);
}

static void run_flecs_chunk_2(int round, int entities)
{
    ecs_world_t* world = ecs_init();
    ECS_COMPONENT(world, A);
    ECS_COMPONENT(world, B);
    run_flecs_chunk_impl(round, entities, 2, world, ecs_id(A), ecs_id(B), 0);
    ecs_fini(world);
}

static void run_flecs_chunk_3(int round, int entities)
{
    ecs_world_t* world = ecs_init();
    ECS_COMPONENT(world, A);
    ECS_COMPONENT(world, B);
    ECS_COMPONENT(world, C);
    run_flecs_chunk_impl(round, entities, 3, world, ecs_id(A), ecs_id(B), ecs_id(C));
    ecs_fini(world);
}

// EnTT 的原生遍历形态是 storage 级 view：实体级回调，无 chunk 概念。
// fresh 每次构造 view（对应 EntJoy 每次 Query<T>()），cached 预建 view（对应 Arch 预建 query）。
static void run_entt_view_impl(int round, int entities, int components, bool cached)
{
    entt::registry registry;
    for (int i = 0; i < entities; ++i)
    {
        entt::entity entity = registry.create();
        registry.emplace<A>(entity, A{0});
        registry.emplace<B>(entity, B{1});
        if (components == 3) registry.emplace<C>(entity, C{2});
    }

    int visited = 0;
    const int expected = components == 2 ? 1 : 3;
    if (components == 2)
    {
        registry.view<A, B>().each([&](A& a, B& b) { a.Value += b.Value; ++visited; });
    }
    else
    {
        registry.view<A, B, C>().each(
            [&](A& a, B& b, C& c) { a.Value += b.Value + c.Value; ++visited; });
    }

    bool ok = visited == entities;
    if (ok)
        registry.view<const A>().each([&](const A& a) { if (a.Value != expected) ok = false; });

    if (!ok)
    {
        std::printf("MATRIX_ERROR framework=EnTT entities=%d components=%d visited=%d\n", entities,
            components, visited);
        return;
    }

    Timing timing;
    if (components == 2)
    {
        if (cached)
        {
            auto view = registry.view<A, B>();
            timing = measure([&]() { view.each([](A& a, B& b) { a.Value += b.Value; }); });
        }
        else
        {
            timing = measure(
                [&]() { registry.view<A, B>().each([](A& a, B& b) { a.Value += b.Value; }); });
        }
    }
    else
    {
        if (cached)
        {
            auto view = registry.view<A, B, C>();
            timing = measure(
                [&]() { view.each([](A& a, B& b, C& c) { a.Value += b.Value + c.Value; }); });
        }
        else
        {
            timing = measure([&]()
                {
                    registry.view<A, B, C>().each(
                        [](A& a, B& b, C& c) { a.Value += b.Value + c.Value; });
                });
        }
    }

    print_result(round, "EnTT", "Query", cached ? "view-each-cached" : "view-each", "Cpp",
        "Compiler", entities, components, 0, timing);
}

// 结构变更：创建 / 加组件 / 移除组件 / 销毁，与 .NET 侧 StructuralBench 同构。
// 复位一律在计时之外，按轮取最小，每轮内 Flecs 与 EnTT 只隔一次复位。
static void run_struct_bench(int n, int rounds)
{
    ecs_world_t* world = ecs_init();
    ECS_COMPONENT(world, A);
    ECS_COMPONENT(world, B);
    ECS_COMPONENT(world, C);
    const ecs_entity_t idA = ecs_id(A);
    const ecs_entity_t idB = ecs_id(B);
    const ecs_entity_t idC = ecs_id(C);

    entt::registry registry;
    std::vector<ecs_entity_t> flecsBuf(static_cast<size_t>(n));
    std::vector<entt::entity> enttBuf(static_cast<size_t>(n));

    const char* ops[4] = {"create", "add", "remove", "destroy"};
    double flecsMin[4];
    double enttMin[4];
    for (int i = 0; i < 4; ++i)
    {
        flecsMin[i] = 1e18;
        enttMin[i] = 1e18;
    }

    // Flecs 没有一次调用建多组件实体的 API：创建等于两次原型迁移，与 .NET 侧
    // NewEntity(c2) 的一次到位不同，这个差异是框架形态，不在此抹平。
    auto flecsMake = [&](int i)
    {
        ecs_entity_t e = ecs_new_w_id(world, idA);
        ecs_add_id(world, e, idB);
        flecsBuf[static_cast<size_t>(i)] = e;
    };
    auto enttMake = [&](int i)
    {
        entt::entity e = registry.create();
        registry.emplace<A>(e, A{0});
        registry.emplace<B>(e, B{1});
        enttBuf[static_cast<size_t>(i)] = e;
    };

    std::printf("STRUCT_HEAD entities=%d rounds=%d framework=native compiler=%s opt=-O%d\n", n,
        rounds, ENTJOY_COMPILER_NAME, ENTJOY_OPT_NUMBER);

    for (int r = 1; r <= rounds; ++r)
    {
        for (int o = 0; o < 4; ++o)
        {
            // ---- Flecs ----
            if (o != 0)
                for (int i = 0; i < n; ++i) flecsMake(i);
            if (o == 2)
                for (int i = 0; i < n; ++i) ecs_add_id(world, flecsBuf[static_cast<size_t>(i)], idC);

            auto start = std::chrono::steady_clock::now();
            if (o == 0) { for (int i = 0; i < n; ++i) flecsMake(i); }
            else if (o == 1) { for (int i = 0; i < n; ++i) ecs_add_id(world, flecsBuf[static_cast<size_t>(i)], idC); }
            else if (o == 2) { for (int i = 0; i < n; ++i) ecs_remove_id(world, flecsBuf[static_cast<size_t>(i)], idC); }
            else { for (int i = 0; i < n; ++i) ecs_delete(world, flecsBuf[static_cast<size_t>(i)]); }
            auto end = std::chrono::steady_clock::now();
            double us = std::chrono::duration<double, std::micro>(end - start).count();

            if (o != 3)
                for (int i = 0; i < n; ++i) ecs_delete(world, flecsBuf[static_cast<size_t>(i)]);
            if (us < flecsMin[o]) flecsMin[o] = us;
            std::printf("STRUCT_ROW op=%s framework=Flecs compiler=%s opt=-O%d round=%d us=%.1f\n",
                ops[o], ENTJOY_COMPILER_NAME, ENTJOY_OPT_NUMBER, r, us);

            // ---- EnTT ----
            if (o != 0)
                for (int i = 0; i < n; ++i) enttMake(i);
            if (o == 2)
                for (int i = 0; i < n; ++i) registry.emplace<C>(enttBuf[static_cast<size_t>(i)], C{2});

            start = std::chrono::steady_clock::now();
            if (o == 0) { for (int i = 0; i < n; ++i) enttMake(i); }
            else if (o == 1) { for (int i = 0; i < n; ++i) registry.emplace<C>(enttBuf[static_cast<size_t>(i)], C{2}); }
            else if (o == 2) { for (int i = 0; i < n; ++i) registry.remove<C>(enttBuf[static_cast<size_t>(i)]); }
            else { for (int i = 0; i < n; ++i) registry.destroy(enttBuf[static_cast<size_t>(i)]); }
            end = std::chrono::steady_clock::now();
            us = std::chrono::duration<double, std::micro>(end - start).count();

            if (o != 3)
                for (int i = 0; i < n; ++i) registry.destroy(enttBuf[static_cast<size_t>(i)]);
            if (us < enttMin[o]) enttMin[o] = us;
            std::printf("STRUCT_ROW op=%s framework=EnTT compiler=%s opt=-O%d round=%d us=%.1f\n",
                ops[o], ENTJOY_COMPILER_NAME, ENTJOY_OPT_NUMBER, r, us);
        }
    }

    for (int i = 0; i < 4; ++i)
        std::printf(
            "STRUCT_RATIO op=%s flecs_us=%.1f entt_us=%.1f ratio=%.4f ns_flecs=%.2f ns_entt=%.2f\n",
            ops[i], flecsMin[i], enttMin[i], flecsMin[i] / enttMin[i], flecsMin[i] * 1000.0 / n,
            enttMin[i] * 1000.0 / n);

    ecs_fini(world);
}

// EnTT 的 owned group：官方推荐的最快遍历形态。group 让被 own 的 storage 顺序一致，
// 遍历时不必逐个查 sparse，是 view 之外的另一条官方路径。
static void run_entt_group_impl(int round, int entities, int components)
{
    entt::registry registry;
    for (int i = 0; i < entities; ++i)
    {
        entt::entity entity = registry.create();
        registry.emplace<A>(entity, A{0});
        registry.emplace<B>(entity, B{1});
        if (components == 3) registry.emplace<C>(entity, C{2});
    }

    int visited = 0;
    const int expected = components == 2 ? 1 : 3;
    if (components == 2)
    {
        registry.view<A, B>().each([&](A& a, B& b) { a.Value += b.Value; ++visited; });
    }
    else
    {
        registry.view<A, B, C>().each(
            [&](A& a, B& b, C& c) { a.Value += b.Value + c.Value; ++visited; });
    }

    bool ok = visited == entities;
    if (ok)
        registry.view<const A>().each([&](const A& a) { if (a.Value != expected) ok = false; });

    if (!ok)
    {
        std::printf("MATRIX_ERROR framework=EnTT entities=%d components=%d visited=%d group\n",
            entities, components, visited);
        return;
    }

    Timing timing;
    if (components == 2)
    {
        auto group = registry.group<A, B>();
        timing = measure([&]() { group.each([](A& a, B& b) { a.Value += b.Value; }); });
    }
    else
    {
        auto group = registry.group<A, B, C>();
        timing = measure(
            [&]() { group.each([](A& a, B& b, C& c) { a.Value += b.Value + c.Value; }); });
    }

    print_result(round, "EnTT", "Query", "group-each", "Cpp", "Compiler", entities, components, 0,
        timing);
}

int main(int argc, char** argv)
{
    if (argc > 1 && std::strcmp(argv[1], "--struct") == 0)
    {
        // 实体数支持逗号分隔的多档，一次运行跑完整个档位矩阵。
        int rounds = argc > 3 ? std::atoi(argv[3]) : 25;
        if (argc > 2)
        {
            std::string list = argv[2];
            size_t start = 0;
            while (start < list.size())
            {
                size_t comma = list.find(',', start);
                std::string token = list.substr(
                    start, comma == std::string::npos ? std::string::npos : comma - start);
                int n = std::atoi(token.c_str());
                if (n > 0) run_struct_bench(n, rounds);
                if (comma == std::string::npos) break;
                start = comma + 1;
            }
        }
        return 0;
    }
    int entitiesList[] = {1024, 100000};
    int componentsList[] = {2, 3};
    int rounds = kRounds;

    for (int i = 1; i < argc; ++i)
    {
        if (std::strncmp(argv[i], "--rounds=", 9) == 0) rounds = std::atoi(argv[i] + 9);
        else if (std::strncmp(argv[i], "--entities=", 11) == 0)
        {
            entitiesList[0] = std::atoi(argv[i] + 11);
            entitiesList[1] = 0;
        }
    }

    std::printf("MATRIX_ENV framework=native compiler=%s compiler_version=%s opt=-O%d "
                "language_std=%ld rounds=%d warmup=%d samples=%d repetitions=%d flecs=4.2.0 "
                "entt=4.0.0\n",
        ENTJOY_COMPILER_NAME, ENTJOY_COMPILER_VERSION, ENTJOY_OPT_NUMBER,
        static_cast<long>(__cplusplus), rounds, kWarmup, kSamples, kRepetitions);

    for (int round = 0; round < rounds; ++round)
    {
        for (int count : entitiesList)
        {
            if (count <= 0) continue;
            for (int components : componentsList)
            {
                // 每轮正逆序交替，与 TraversalMatrix 的交错方式一致。
                const int armCount = 4;
                for (int index = 0; index < armCount; ++index)
                {
                    int arm = round % 2 == 0 ? index : armCount - 1 - index;
                    if (components == 2)
                    {
                        if (arm == 0) run_flecs_chunk_2(round + 1, count);
                        else if (arm == 3) run_entt_group_impl(round + 1, count, 2);
                        else run_entt_view_impl(round + 1, count, 2, arm == 2);
                    }
                    else
                    {
                        if (arm == 0) run_flecs_chunk_3(round + 1, count);
                        else if (arm == 3) run_entt_group_impl(round + 1, count, 3);
                        else run_entt_view_impl(round + 1, count, 3, arm == 2);
                    }
                }
                std::printf(
                    "MATRIX_UNAVAILABLE round=%d framework=EnTT layer=Chunk api=none entities=%d "
                    "components=%d reason=no_chunk_level_api\n",
                    round + 1, count, components);
            }
        }
    }

    return 0;
}
