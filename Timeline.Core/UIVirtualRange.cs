using System;
using System.Collections.Generic;

namespace Timeline
{
    // UI-only range math. PlaybackLookup and authoritative SortedLists stay unchanged.
    internal static class UIVirtualRange
    {
        internal const float Epsilon = 0.001f;
        internal const float HorizontalOverscan = 64f;
        internal static int LowerBound(IList<float> sorted, float value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi) { int mid = lo + ((hi - lo) >> 1); if (sorted[mid] < value) lo = mid + 1; else hi = mid; }
            return lo;
        }
        internal static int UpperBound(IList<float> sorted, float value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi) { int mid = lo + ((hi - lo) >> 1); if (sorted[mid] <= value) lo = mid + 1; else hi = mid; }
            return lo;
        }
        // Bottoms are cumulative, so headers and tracks can have different heights.
        internal static void Rows(IList<float> bottoms, float top, float bottom, float overscan, out int first, out int end)
        {
            first = LowerBound(bottoms, top - overscan - Epsilon);
            end = Math.Min(bottoms.Count, UpperBound(bottoms, bottom + overscan + Epsilon) + 1);
            if (bottom + overscan + Epsilon < 0f) end = 0;
            if (bottom < top) end = first;
        }
        internal static void Keys(IList<float> times, float start, float finish, out int first, out int end)
        {
            first = LowerBound(times, start - Epsilon);
            end = UpperBound(times, finish + Epsilon);
            if (finish < start) end = first;
        }
        internal static int PoolTarget(int demand, int reserve)
        {
            return demand + Math.Max(reserve, (demand + 3) / 4);
        }
    }
}
