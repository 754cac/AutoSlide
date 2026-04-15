using System.Collections.Generic;
using System.Linq;

namespace PowerPointSharing
{
    internal sealed class IntervalSet
    {
        private readonly List<Interval> _intervals = new List<Interval>();

        public void Clear()
        {
            _intervals.Clear();
        }

        public void Add(int value)
        {
            AddRange(value, value);
        }

        public void AddRange(int startInclusive, int endInclusive)
        {
            if (endInclusive < startInclusive)
            {
                var temp = startInclusive;
                startInclusive = endInclusive;
                endInclusive = temp;
            }

            var next = new Interval(startInclusive, endInclusive);
            var merged = new List<Interval>();
            var inserted = false;

            foreach (var current in _intervals)
            {
                if (current.End < next.Start - 1)
                {
                    merged.Add(current);
                    continue;
                }

                if (next.End < current.Start - 1)
                {
                    if (!inserted)
                    {
                        merged.Add(next);
                        inserted = true;
                    }

                    merged.Add(current);
                    continue;
                }

                next = new Interval(
                    next.Start < current.Start ? next.Start : current.Start,
                    next.End > current.End ? next.End : current.End);
            }

            if (!inserted)
                merged.Add(next);

            _intervals.Clear();
            _intervals.AddRange(merged.OrderBy(i => i.Start));
        }

        public bool Contains(int value)
        {
            foreach (var interval in _intervals)
            {
                if (value < interval.Start)
                    return false;

                if (value <= interval.End)
                    return true;
            }

            return false;
        }

        public IReadOnlyList<(int Start, int End)> Snapshot()
        {
            return _intervals.Select(i => (i.Start, i.End)).ToList();
        }

        private sealed class Interval
        {
            public Interval(int start, int end)
            {
                Start = start;
                End = end;
            }

            public int Start { get; }
            public int End { get; }
        }
    }
}
