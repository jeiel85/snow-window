namespace SnowWindow.Simulation;

internal static class Intervals
{
    /// <summary>
    /// 서로 겹치지 않고 정렬된 [A, B) 구간 목록에서 [cutA, cutB)를 빼낸다. 정렬 순서는 유지된다.
    /// </summary>
    public static void Subtract(List<(float A, float B)> list, float cutA, float cutB)
    {
        if (cutB <= cutA)
        {
            return;
        }

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var (a, b) = list[i];
            if (cutB <= a || cutA >= b)
            {
                continue;
            }

            list.RemoveAt(i);
            if (cutB < b)
            {
                list.Insert(i, (cutB, b));
            }

            if (cutA > a)
            {
                list.Insert(i, (a, cutA));
            }
        }
    }
}
