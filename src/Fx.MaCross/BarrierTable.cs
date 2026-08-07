namespace Fx.MaCross;

internal sealed class BarrierTable
{
    public int[][] Up = [];
    public int[][] Down = [];

    public static BarrierTable Build(History h, int[] distancePoints, int threads, Action<string>? log = null)
    {
        int n = h.Count;
        int d = distancePoints.Length;
        var table = new BarrierTable
        {
            Up = new int[d][],
            Down = new int[d][],
        };
        for (int k = 0; k < d; k++)
        {
            table.Up[k] = new int[n];
            table.Down[k] = new int[n];
        }

        int stackCap = Math.Min(n, h.MaxPrice - h.MinPrice + 2) + 1;
        int chunks = Math.Max(threads, 1) * 2;
        int chunkSize = (n + chunks - 1) / chunks;

        var jobs = new List<(bool Up, int From, int To)>();
        for (int start = 0; start < n; start += chunkSize)
        {
            int end = Math.Min(n, start + chunkSize);
            jobs.Add((true, start, end));
            jobs.Add((false, start, end));
        }

        log?.Invoke($"  {jobs.Count} jobs, stack capacity {stackCap:N0}");

        int next = -1;
        var workers = new Task[Math.Min(threads, jobs.Count)];
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t] = Task.Run(() =>
            {
                var stackIndex = new int[stackCap];
                var stackValue = new int[stackCap];
                while (true)
                {
                    int j = Interlocked.Increment(ref next);
                    if (j >= jobs.Count) break;
                    var job = jobs[j];
                    if (job.Up)
                        BuildUp(h.Hi, h.Avg, n, distancePoints, table.Up, job.From, job.To, stackIndex, stackValue);
                    else
                        BuildDown(h.Lo, h.Avg, n, distancePoints, table.Down, job.From, job.To, stackIndex, stackValue);
                }
            });
        }

        Task.WaitAll(workers);
        return table;
    }

    private static void BuildUp(int[] hi, int[] avg, int n, int[] dist, int[][] table, int from, int to,
        int[] stackIndex, int[] stackValue)
    {
        int d = dist.Length;
        int sp = 0;
        for (int i = n - 1; i >= from; i--)
        {
            int c = i + 1;
            if (c < n)
            {
                int v = hi[c];
                while (sp > 0 && stackValue[sp - 1] <= v) sp--;
                stackIndex[sp] = c;
                stackValue[sp] = v;
                sp++;
            }

            if (i >= to) continue;

            int price = avg[i];
            int high = sp - 1;
            for (int k = 0; k < d; k++)
            {
                long x = (long)price + dist[k];
                if (sp == 0 || stackValue[0] < x)
                {
                    for (int rest = k; rest < d; rest++) table[rest][i] = n;
                    break;
                }

                int lo = 0;
                int hiPos = high;
                while (lo < hiPos)
                {
                    int mid = (lo + hiPos + 1) >> 1;
                    if (stackValue[mid] >= x) lo = mid;
                    else hiPos = mid - 1;
                }

                table[k][i] = stackIndex[lo];
                high = lo;
            }
        }
    }

    private static void BuildDown(int[] lo, int[] avg, int n, int[] dist, int[][] table, int from, int to,
        int[] stackIndex, int[] stackValue)
    {
        int d = dist.Length;
        int sp = 0;
        for (int i = n - 1; i >= from; i--)
        {
            int c = i + 1;
            if (c < n)
            {
                int v = lo[c];
                while (sp > 0 && stackValue[sp - 1] >= v) sp--;
                stackIndex[sp] = c;
                stackValue[sp] = v;
                sp++;
            }

            if (i >= to) continue;

            int price = avg[i];
            int high = sp - 1;
            for (int k = 0; k < d; k++)
            {
                long x = (long)price - dist[k];
                if (sp == 0 || stackValue[0] > x)
                {
                    for (int rest = k; rest < d; rest++) table[rest][i] = n;
                    break;
                }

                int low = 0;
                int hiPos = high;
                while (low < hiPos)
                {
                    int mid = (low + hiPos + 1) >> 1;
                    if (stackValue[mid] <= x) low = mid;
                    else hiPos = mid - 1;
                }

                table[k][i] = stackIndex[low];
                high = low;
            }
        }
    }
}
