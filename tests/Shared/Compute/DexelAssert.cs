using ThreeCTool.Compute;

namespace ThreeCTool.TestSupport;

internal static class DexelAssert
{
    public static void GridsClose(DexelGrid expected, DexelGrid actual, float tolerance, int allowedMismatches = 0)
    {
        var mismatches = 0;
        var worst = 0f;
        var detail = string.Empty;
        for (var iv = 0; iv < expected.CountV; iv++)
        {
            for (var iu = 0; iu < expected.CountU; iu++)
            {
                var a = expected.Ray(iu, iv);
                var b = actual.Ray(iu, iv);
                var same = a.Length == b.Length;
                for (var k = 0; same && k < a.Length; k++)
                {
                    same = MathF.Abs(a[k] - b[k]) <= tolerance;
                }

                // Tangential touches may create or drop a sliver segment on one side only.
                var difference = MathF.Abs(Length(a) - Length(b));
                if (!same && difference > 4 * tolerance)
                {
                    mismatches++;
                    if (difference > worst)
                    {
                        worst = difference;
                        detail = $"({iu},{iv}) [{string.Join(", ", a.ToArray())}] vs [{string.Join(", ", b.ToArray())}]";
                    }
                }
            }
        }

        Assert.True(mismatches <= allowedMismatches, $"{expected.Axis}: {mismatches} rays differ, worst {detail}");
    }

    private static float Length(ReadOnlySpan<float> ray)
    {
        var total = 0f;
        for (var k = 0; k < ray.Length; k += 2)
        {
            total += ray[k + 1] - ray[k];
        }

        return total;
    }
}
