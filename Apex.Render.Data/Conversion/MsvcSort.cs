namespace Apex.Render.Data.Conversion;

/// <summary>
/// A strict-weak "less than" predicate over elements of a span (by reference so large structs are not copied).
/// </summary>
public delegate bool LessThan<T>(in T a, in T b);

/// <summary>A <see cref="LessThan{T}"/> as a struct, so <see cref="MsvcSort"/> can inline the comparison.</summary>
public interface ILessThan<T>
{
    bool Less(in T a, in T b);
}

/// <summary>
/// Bit-for-bit replica of the MSVC (VS2012/2013 era) <c>std::sort</c> APE is built with: introsort with
/// <c>_ISORT_MAX = 32</c>, median-of-three / Tukey ninther (&gt; 40 elements) pivots, the "fat partition"
/// <c>_Unguarded_partition</c>, a <c>1.5 log2(N)</c> depth budget and a heap-sort fallback.
/// <para>
/// <c>std::sort</c> is not stable, so wherever APE sorts elements with equal keys the resulting order depends
/// on this exact algorithm; converting the same inputs must reproduce it (conversion.md §1.9).
/// Verified against the partition routines in the exe (e.g. 0x14068EBD0).
/// </para>
/// </summary>
public static class MsvcSort
{
    private const int IsortMax = 32;

    /// <summary><c>std::sort(first, last, pred)</c>.</summary>
    public static void Sort<T>(Span<T> a, LessThan<T> pred) => Sort(a, new DelegateLess<T>(pred));

    /// <summary><c>std::sort(first, last, pred)</c> with a struct predicate.</summary>
    public static void Sort<T, TLess>(Span<T> a, TLess pred) where TLess : struct, ILessThan<T> =>
        SortRange(a, 0, a.Length, a.Length, pred);

    private readonly struct DelegateLess<T>(LessThan<T> pred) : ILessThan<T>
    {
        public bool Less(in T a, in T b) => pred(a, b);
    }

    private static void SortRange<T, TLess>(Span<T> a, int first, int last, long ideal, TLess pred) where TLess : struct, ILessThan<T>
    {
        int count;
        for (; IsortMax < (count = last - first) && 0 < ideal;)
        {
            var (mFirst, mSecond) = UnguardedPartition(a, first, last, pred);
            ideal /= 2;
            ideal += ideal / 2;

            if (mFirst - first < last - mSecond)
            {
                SortRange(a, first, mFirst, ideal, pred);
                first = mSecond;
            }
            else
            {
                SortRange(a, mSecond, last, ideal, pred);
                last = mFirst;
            }
        }

        if (IsortMax < count)
        {
            MakeHeap(a, first, last, pred);
            for (; 1 < last - first; --last)
                PopHeap(a, first, last, pred);
        }
        else if (1 < count)
            InsertionSort(a, first, last, pred);
    }

    private static void Swap<T>(Span<T> a, int i, int j) => (a[i], a[j]) = (a[j], a[i]);

    private static void InsertionSort<T, TLess>(Span<T> a, int first, int last, TLess pred) where TLess : struct, ILessThan<T>
    {
        if (first == last)
            return;
        for (int next = first; ++next != last;)
        {
            int next1 = next;
            T val = a[next];
            if (pred.Less(val, a[first]))
            {
                for (int i = next; i > first; i--)
                    a[i] = a[i - 1];
                a[first] = val;
            }
            else
            {
                for (int first1 = next1; pred.Less(val, a[--first1]); next1 = first1)
                    a[next1] = a[first1];
                a[next1] = val;
            }
        }
    }

    private static void Med3<T, TLess>(Span<T> a, int first, int mid, int last, TLess pred) where TLess : struct, ILessThan<T>
    {
        if (pred.Less(a[mid], a[first]))
            Swap(a, mid, first);
        if (pred.Less(a[last], a[mid]))
        {
            Swap(a, last, mid);
            if (pred.Less(a[mid], a[first]))
                Swap(a, mid, first);
        }
    }

    private static void Median<T, TLess>(Span<T> a, int first, int mid, int last, TLess pred) where TLess : struct, ILessThan<T>
    {
        if (40 < last - first)
        {
            int step = (last - first + 1) / 8;
            Med3(a, first, first + step, first + 2 * step, pred);
            Med3(a, mid - step, mid, mid + step, pred);
            Med3(a, last - 2 * step, last - step, last, pred);
            Med3(a, first + step, mid, last - step, pred);
        }
        else
            Med3(a, first, mid, last, pred);
    }

    private static (int, int) UnguardedPartition<T, TLess>(Span<T> a, int first, int last, TLess pred) where TLess : struct, ILessThan<T>
    {
        int mid = first + (last - first) / 2;
        Median(a, first, mid, last - 1, pred);
        int pfirst = mid;
        int plast = pfirst + 1;

        while (first < pfirst && !pred.Less(a[pfirst - 1], a[pfirst]) && !pred.Less(a[pfirst], a[pfirst - 1]))
            --pfirst;
        while (plast < last && !pred.Less(a[plast], a[pfirst]) && !pred.Less(a[pfirst], a[plast]))
            ++plast;

        int gfirst = plast;
        int glast = pfirst;

        for (;;)
        {
            for (; gfirst < last; ++gfirst)
            {
                if (pred.Less(a[pfirst], a[gfirst]))
                    ;
                else if (pred.Less(a[gfirst], a[pfirst]))
                    break;
                else if (plast++ != gfirst)
                    Swap(a, plast - 1, gfirst);
            }
            for (; first < glast; --glast)
            {
                if (pred.Less(a[glast - 1], a[pfirst]))
                    ;
                else if (pred.Less(a[pfirst], a[glast - 1]))
                    break;
                else if (--pfirst != glast - 1)
                    Swap(a, pfirst, glast - 1);
            }
            if (glast == first && gfirst == last)
                return (pfirst, plast);

            if (glast == first)
            {
                if (plast != gfirst)
                    Swap(a, pfirst, plast);
                ++plast;
                Swap(a, pfirst++, gfirst++);
            }
            else if (gfirst == last)
            {
                if (--glast != --pfirst)
                    Swap(a, glast, pfirst);
                Swap(a, pfirst, --plast);
            }
            else
                Swap(a, gfirst++, --glast);
        }
    }

    private static void PushHeap<T, TLess>(Span<T> a, int first, int hole, int top, T val, TLess pred) where TLess : struct, ILessThan<T>
    {
        for (int idx = (hole - 1) / 2; top < hole && pred.Less(a[first + idx], val); idx = (hole - 1) / 2)
        {
            a[first + hole] = a[first + idx];
            hole = idx;
        }
        a[first + hole] = val;
    }

    private static void AdjustHeap<T, TLess>(Span<T> a, int first, int hole, int bottom, T val, TLess pred) where TLess : struct, ILessThan<T>
    {
        int top = hole;
        int idx = 2 * hole + 2;
        for (; idx < bottom; idx = 2 * idx + 2)
        {
            if (pred.Less(a[first + idx], a[first + idx - 1]))
                --idx;
            a[first + hole] = a[first + idx];
            hole = idx;
        }
        if (idx == bottom)
        {
            a[first + hole] = a[first + bottom - 1];
            hole = bottom - 1;
        }
        PushHeap(a, first, hole, top, val, pred);
    }

    private static void MakeHeap<T, TLess>(Span<T> a, int first, int last, TLess pred) where TLess : struct, ILessThan<T>
    {
        int bottom = last - first;
        for (int hole = bottom / 2; 0 < hole;)
        {
            --hole;
            T val = a[first + hole];
            AdjustHeap(a, first, hole, bottom, val, pred);
        }
    }

    private static void PopHeap<T, TLess>(Span<T> a, int first, int last, TLess pred) where TLess : struct, ILessThan<T>
    {
        T val = a[last - 1];
        a[last - 1] = a[first];
        AdjustHeap(a, first, 0, last - 1 - first, val, pred);
    }
}
