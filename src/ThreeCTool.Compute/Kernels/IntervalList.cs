using ILGPU;

namespace ThreeCTool.Compute.Kernels;

/// <summary>Sorted, disjoint interval lists stored as (start, end) pairs in a fixed-capacity device array.</summary>
internal static class IntervalList
{
    /// <summary>Unions [a, b] into the list. On overflow the gap to a neighbour is closed (keeps more coverage).</summary>
    public static int Insert(ArrayView<float> list, int count, int capacity, float a, float b, ref bool overflow)
    {
        // Find the first interval ending at or after a.
        var first = 0;
        while (first < count && list[(2 * first) + 1] < a)
        {
            first++;
        }

        // Absorb all intervals starting at or before b.
        var last = first;
        while (last < count && list[2 * last] <= b)
        {
            a = MathF.Min(a, list[2 * last]);
            b = MathF.Max(b, list[(2 * last) + 1]);
            last++;
        }

        var removed = last - first;
        if (removed == 0 && count == capacity)
        {
            overflow = true;
            if (first > 0)
            {
                list[(2 * (first - 1)) + 1] = MathF.Max(list[(2 * (first - 1)) + 1], b);
                return count;
            }

            list[0] = a;
            return count;
        }

        // Shift the tail so exactly one slot replaces [first, last).
        var shift = 1 - removed;
        if (shift > 0)
        {
            for (var k = count - 1; k >= last; k--)
            {
                list[2 * (k + shift)] = list[2 * k];
                list[(2 * (k + shift)) + 1] = list[(2 * k) + 1];
            }
        }
        else if (shift < 0)
        {
            for (var k = last; k < count; k++)
            {
                list[2 * (k + shift)] = list[2 * k];
                list[(2 * (k + shift)) + 1] = list[(2 * k) + 1];
            }
        }

        list[2 * first] = a;
        list[(2 * first) + 1] = b;
        return count + shift;
    }

    /// <summary>
    /// Removes (a, b) from the list. A split that does not fit leaves the hole unremoved instead (keeps more coverage).
    /// Pieces shorter than <paramref name="minLength"/> are dropped.
    /// </summary>
    public static int Subtract(ArrayView<float> list, int count, int capacity, float a, float b, float minLength, ref bool overflow)
    {
        var k = 0;
        while (k < count)
        {
            var x = list[2 * k];
            var y = list[(2 * k) + 1];
            var next = k + 1;
            if (y > a && x < b)
            {
                var keepLeft = a - x >= minLength;
                var keepRight = y - b >= minLength;
                if (keepLeft && keepRight)
                {
                    if (count < capacity)
                    {
                        for (var j = count - 1; j > k; j--)
                        {
                            list[2 * (j + 1)] = list[2 * j];
                            list[(2 * (j + 1)) + 1] = list[(2 * j) + 1];
                        }

                        list[(2 * k) + 1] = a;
                        list[2 * (k + 1)] = b;
                        list[(2 * (k + 1)) + 1] = y;
                        count++;
                        next = k + 2;
                    }
                    else
                    {
                        overflow = true;
                    }
                }
                else if (keepLeft)
                {
                    list[(2 * k) + 1] = a;
                }
                else if (keepRight)
                {
                    list[2 * k] = b;
                }
                else
                {
                    for (var j = k + 1; j < count; j++)
                    {
                        list[2 * (j - 1)] = list[2 * j];
                        list[(2 * (j - 1)) + 1] = list[(2 * j) + 1];
                    }

                    count--;
                    next = k;
                }
            }

            k = next;
        }

        return count;
    }
}
