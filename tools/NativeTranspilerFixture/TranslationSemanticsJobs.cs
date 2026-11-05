using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationVariantJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public bool First, Second;

    public void Execute(int index)
    {
        int value = index;
        if (First) value += 1;
        if (Second) value += 2;
        Output[index] = value;
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationBooleanChainJob : IJobParallelFor
{
    public NativeArray<int> Output;

    public void Execute(int index)
    {
        int result = 0;
        if (false || (index & 1) == 0) result += 1;
        if ((index & 1) != 0 || false) result += 2;
        if (true && index < 3) result += 4;
        Output[index] = result;
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationMaskNegationJob : IJobParallelFor
{
    public NativeArray<float> Input;
    public NativeArray<int> Output;
    public int Iterations;

    public void Execute(int index)
    {
        float value = Input[index];
        int count = 0;
        for (int i = 0; i < Iterations; i++)
        {
            if (value < 0f) continue;
            count++;
        }
        if ((index & 1) != 0 && value == value) { }
        else count += 1000;
        Output[index] = count;
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationConditionalSelectionJob : IJobParallelFor
{
    public NativeArray<float> Input, Candidate, Output;

    public void Execute(int index)
    {
        float best = Input[index];
        float candidate = Candidate[index];
        if (best < candidate) best = candidate;
        Output[index] = best;
    }
}

[NativeTranspile]
public unsafe struct TranslationPointerShadowJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public int* Data;

    public void Execute(int index)
    {
        int Data = index + 7;
        Output[index] = Data;
    }
}

[NativeTranspile]
public struct TranslationScalarShadowJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public int Bias;

    public void Execute(int index)
    {
        int Bias = index + 2;
        Output[index] = Bias + this.Bias;
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationSimdScalarShadowJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public int Bias;

    public void Execute(int index)
    {
        int value = Bias;
        if (index >= 0)
        {
            int Bias = index + 2;
            value += Bias;
        }
        Output[index] = value + Bias;
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationMaskedStoreJob : IJobParallelFor
{
    public NativeArray<int> Input, Output, Visits;

    public void Execute(int index)
    {
        int choice = index;
        Output[index] = -1;
        if ((index & 1) == 0) Output[index] = Input[choice];
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationSiblingStoreJob : IJobParallelFor
{
    public NativeArray<int> Input, Output, Visits;

    public void Execute(int index)
    {
        int choice = index;
        if ((index & 1) == 0) Visits[index] = 123;
        else Visits[index] = 456;
        Output[index] = Input[choice];
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationBoolShadowJob : IJobParallelFor
{
    public NativeArray<int> Output;
    public bool Active;

    public void Execute(int index)
    {
        if (this.Active)
        {
            bool Active = (index & 1) == 0;
            Active = !Active;
            bool other = index % 3 == 0;
            bool result = Active && other;
            bool selected = other ? Active : !Active;
            Output[index] = (result || selected) && (Active == other) ? 11 : 22;
        }
        else Output[index] = -1;
    }
}

[NativeTranspile]
public struct TranslationWideArithmeticJob : IJobParallelFor
{
    public NativeArray<long> Input;
    public NativeArray<int> Output;

    public void Execute(int index)
    {
        long value = Input[index];
        long negative = -value;
        long next = value;
        long old = next++;
        long previous = value;
        --previous;
        long product = value;
        product *= 2;
        Output[index] = (value < 0 && negative < 0 ? 1 : 0)
            | (next < old ? 2 : 0) | (previous > value ? 4 : 0)
            | (value > 0 && product < 0 ? 8 : 0);
    }
}

[NativeTranspile]
public struct TranslationMixedArithmeticJob : IJobParallelFor
{
    public NativeArray<long> Output;

    public void Execute(int index)
    {
        uint value = uint.MaxValue;
        int signedValue = -1;
        Output[index] = value * signedValue;
    }
}

[NativeTranspile]
public struct TranslationEvaluationJob : IJob
{
    public NativeArray<int> Output;

    public void Execute()
    {
        int cursor = 0;
        Output[cursor++] += cursor;
        Output[cursor++] += cursor;
        Output[2] = cursor;
    }
}

[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct TranslationReductionBoundsJob : IJobParallelFor
{
    public NativeArray<int> Starts, Ends, Output;
    public int Mode;

    public void Execute(int index)
    {
        int best = int.MaxValue;
        int end = Ends[index];
        if (Mode == 0)
        {
            for (int i = Starts[index]; i < end; i++)
            {
                int candidate = Starts[index] - i;
                if (candidate < best) best = candidate;
                if ((i & 1) == 0) break;
            }
        }
        else if (Mode == 1)
        {
            for (int i = Starts[index]; i <= end; i++)
            {
                int candidate = Starts[index] - i;
                if (candidate < best) best = candidate;
            }
        }
        else if (Mode == 2)
        {
            for (int i = Starts[index]; i < end; i++)
            {
                int candidate = Starts[index] - i;
                if (candidate < best) best = candidate;
                end--;
            }
        }
        else
        {
            for (int i = Starts[index]; i < end; i++)
            {
                int candidate = Starts[index] - i;
                if (candidate < best) best = candidate;
            }
        }
        Output[index] = best;
    }
}
