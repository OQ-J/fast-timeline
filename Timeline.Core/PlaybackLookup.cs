using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Timeline
{
    // Private implementation only. SortedList remains the authoritative storage.
    internal sealed class PlaybackTrackState
    {
        internal int SegmentIndex = -1;
        internal int ObservedCount = -1;
        internal float LastTime;
        internal KeyValuePair<float, Keyframe> Left;
        internal KeyValuePair<float, Keyframe> Right;

        internal void Locate(SortedList<float, Keyframe> keys, float time,
            out KeyValuePair<float, Keyframe> left, out KeyValuePair<float, Keyframe> right)
        {
            int count = keys.Count;
            bool valid = ObservedCount == count && SegmentIndex >= -1 && SegmentIndex < count;
            if (valid && SegmentIndex >= 0)
                valid = keys.Keys[SegmentIndex].Equals(Left.Key) && ReferenceEquals(keys.Values[SegmentIndex], Left.Value);
            int next = SegmentIndex + 1;
            if (valid && next < count)
                valid = keys.Keys[next].Equals(Right.Key) && ReferenceEquals(keys.Values[next], Right.Value);

            // NaN keys/time reproduce the original <= loop's immediate first-key exit.
            bool unusual = float.IsNaN(time) || (count != 0 && float.IsNaN(keys.Keys[0]));
            if (!valid || unusual || time < LastTime)
                SegmentIndex = PlaybackLookup.LeftIndex(keys, time);
            else if (next < count && keys.Keys[next] <= time)
            {
                int steps = 0;
                while (next < count && keys.Keys[next] <= time && steps < 8)
                {
                    SegmentIndex = next++;
                    steps++;
                }
                if (next < count && keys.Keys[next] <= time)
                    SegmentIndex = PlaybackLookup.LeftIndex(keys, time);
            }
            else if (SegmentIndex >= 0 && !(keys.Keys[SegmentIndex] <= time))
                SegmentIndex = PlaybackLookup.LeftIndex(keys, time);

            ObservedCount = count;
            LastTime = time;
            Left = SegmentIndex >= 0 ? new KeyValuePair<float, Keyframe>(keys.Keys[SegmentIndex], keys.Values[SegmentIndex]) : default(KeyValuePair<float, Keyframe>);
            next = SegmentIndex + 1;
            Right = next < count ? new KeyValuePair<float, Keyframe>(keys.Keys[next], keys.Values[next]) : default(KeyValuePair<float, Keyframe>);
            left = Left;
            right = Right;
        }
    }

    internal static class PlaybackLookup
    {
        internal static int FirstGreaterIndex(SortedList<float, Keyframe> keys, float time)
        {
            int lo = 0, hi = keys.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (keys.Keys[mid] > time) hi = mid;
                else lo = mid + 1;
            }
            return lo;
        }
        // Upper bound: last key <= time, then first key > time. Empty returns -1.
        internal static int LeftIndex(SortedList<float, Keyframe> keys, float time)
        {
            int lo = 0, hi = keys.Count;
            if (hi != 0 && (float.IsNaN(time) || float.IsNaN(keys.Keys[0]))) return -1;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (keys.Keys[mid] <= time) lo = mid + 1;
                else hi = mid;
            }
            return lo - 1;
        }

        internal static Keyframe FindOccupant(SortedList<float, Keyframe> keys, float time, Keyframe self)
        {
            if (float.IsNaN(time) || float.IsInfinity(time)) return null;
            // Find the *first* approximately equal key, preserving ascending priority.
            // A dense float range can contain more than three approximately equal keys.
            int lo = 0, hi = keys.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                float candidateTime = keys.Keys[mid];
                if (candidateTime.CompareTo(time) < 0 && !Mathf.Approximately(candidateTime, time)) lo = mid + 1;
                else hi = mid;
            }
            for (int i = lo; i < keys.Count && Mathf.Approximately(keys.Keys[i], time); i++)
            {
                Keyframe candidate = keys.Values[i];
                if (candidate != self) return candidate;
            }
            return null;
        }
    }

    internal static class CollectionVersion<T>
    {
        // Built once, no per-frame reflection/boxing. Old Mono names are supported.
        internal static readonly Func<T, int> Read = Create();
        private static Func<T, int> Create()
        {
            try
            {
                FieldInfo field = typeof(T).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? typeof(T).GetField("version", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(int)) return null;
                DynamicMethod method = new DynamicMethod("TimelineCollectionVersion", typeof(int), new[] { typeof(T) }, typeof(CollectionVersion<T>), true);
                ILGenerator il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
                var reader = (Func<T, int>)method.CreateDelegate(typeof(Func<T, int>));
                // Force Mono to JIT now; a visibility failure must trigger fallback,
                // rather than first appearing during playback.
                try { reader(default(T)); } catch (NullReferenceException) { }
                return reader;
            }
            catch { return null; } // Unsupported runtime keeps original traversal/search.
        }
    }

    internal sealed class KeyframeTimeCache
    {
        private readonly Dictionary<Keyframe, float> _times = new Dictionary<Keyframe, float>();
        private readonly HashSet<Keyframe> _duplicates = new HashSet<Keyframe>();
        private int _version;
        private bool _built;
        internal int FindIndex(SortedList<float, Keyframe> keys, Keyframe key)
        {
            Func<SortedList<float, Keyframe>, int> read = CollectionVersion<SortedList<float, Keyframe>>.Read;
            if (read == null) return keys.IndexOfValue(key);
            int version = read(keys);
            if (!_built || version != _version)
            {
                _times.Clear(); _duplicates.Clear();
                for (int i = 0; i < keys.Count; i++)
                {
                    Keyframe value = keys.Values[i];
                    if (value == null) continue;
                    if (_times.ContainsKey(value)) _duplicates.Add(value);
                    else _times.Add(value, keys.Keys[i]);
                }
                _built = true; _version = version;
            }
            float time;
            if (_times.TryGetValue(key, out time))
            {
                int index = keys.IndexOfKey(time);
                if (index >= 0 && ReferenceEquals(keys.Values[index], key)) return index;
            }
            return keys.IndexOfValue(key);
        }
        internal void Moved(SortedList<float, Keyframe> keys, Keyframe key, float time)
        {
            if (_duplicates.Contains(key)) { _built = false; return; }
            _times[key] = time;
            var read = CollectionVersion<SortedList<float, Keyframe>>.Read;
            if (read != null) _version = read(keys);
        }
    }
}
