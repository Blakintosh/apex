namespace Apex.Render.Data.Conversion.XModel;

/// <summary>
/// APE's triangle-order optimiser (0x14021E2F0, called from the xmesh converter with weights
/// {1.0, 0.5, 1.3} and a 256-triangle interleave): a greedy fan builder over a 14-entry FIFO vertex cache,
/// followed by an interleave of 256-triangle blocks from the two halves of the list.
/// </summary>
internal static class VertexCacheOptimizer
{
    private const int CacheSize = 14;

    /// <param name="indices">3 * triCount vertex indices.</param>
    /// <param name="triCount">Triangle count.</param>
    /// <param name="wMiss">Weight of cache misses (1.0).</param>
    /// <param name="wValence">Weight of the remaining valence (0.5).</param>
    /// <param name="wPosition">Weight of the FIFO position (1.3).</param>
    /// <param name="block">Interleave block in triangles (256).</param>
    public static uint[] Optimize(uint[] indices, int triCount, float wMiss, float wValence, float wPosition, int block)
    {
        int n3 = 3 * triCount;
        var output = new uint[n3];
        Array.Fill(output, 0xFFFFFFFFu);

        uint maxIndex = 0;
        for (int i = 0; i < n3; i++)
            if (indices[i] > maxIndex) maxIndex = indices[i];
        int offCount = (int)maxIndex + 2;

        // Vertex -> triangle adjacency (0x14021EC70), degenerate repeats dropped.
        var offsets = new int[offCount];
        var adj = new int[n3];
        var clean = BuildAdjacency(indices, triCount, adj, offsets);

        var fan = new FanState(triCount, offsets.Length - 1);
        Array.Fill(fan.Live, true);

        // The exe recounts live triangles per vertex and keeps a sorted list of the vertices that still have some;
        // both are tracked incrementally here. The fallback pick (fewest live triangles, lowest index first) comes
        // from a min-queue on (count, vertex) whose stale entries are skipped: counts only ever go down.
        int maxFan = 0;
        for (int v = 0; v + 1 < offsets.Length; v++)
        {
            int c = offsets[v + 1] - offsets[v];
            fan.Count[v] = c;
            maxFan = Math.Max(maxFan, c);
            if (c > 0)
            {
                fan.Active++;
                fan.Fallback.Enqueue(v, Key(c, v));
            }
        }
        fan.List = new int[maxFan];

        // FIFO window; the exe keeps it in a 1000-int scratch buffer, valid entries [start, end] inclusive.
        // Pushes only ever write past the end before reading, so stale entries beyond the window never matter.
        var buf = new int[Math.Max(1000, CacheSize + 3 * maxFan + 8)];
        Array.Fill(buf, -1);
        int outPos = 0;

        while (fan.Active > 0)
        {
            float bestScore = float.MaxValue;
            int best = -1;
            // Score every vertex in the window (0x14021F1A0).
            for (int w = 0; w < CacheSize; w++)
            {
                int v = buf[w];
                if (v == -1)
                    continue;
                int liveCount = fan.Count[v];
                if (liveCount == 0)
                    continue;
                int simStart = 0, simEnd = CacheSize - 1;
                int misses = EmitFan(buf, ref simStart, ref simEnd, fan, adj, offsets[v], offsets[v + 1], indices, clean, null, ref outPos);
                int pos = 0;
                for (int p = simStart; p <= simEnd; p++, pos++)
                    if (buf[p] == v) break;
                float score = (float)misses * wMiss - (float)liveCount * wValence + ((float)pos * wPosition) / 14.0f;
                if (bestScore > score)
                {
                    best = v;
                    bestScore = score;
                }
            }
            if (best == -1)
            {
                while (fan.Fallback.TryPeek(out int v, out long key) && key != Key(fan.Count[v], v))
                    fan.Fallback.Dequeue();
                best = fan.Fallback.Peek();
            }

            int start = 0, end = CacheSize - 1;
            EmitFan(buf, ref start, ref end, fan, adj, offsets[best], offsets[best + 1], indices, clean, output, ref outPos);

            // Compact the window back to the front of the buffer.
            for (int i = 0; i < CacheSize; i++)
                buf[i] = buf[start + i];
        }

        // Interleave 256-triangle blocks from the two halves.
        int blocks = triCount / block + (triCount % block != 0 ? 1 : 0);
        int ra = 0, rb = 3 * block * (blocks >> 1);
        if ((blocks & 1) != 0)
        {
            ra = 3 * block * (blocks >> 1);
            rb = 0;
        }
        int chunk = 3 * block;
        var result = new uint[n3];
        int written = 0;
        while (written < n3)
        {
            int c1 = Math.Min(chunk, n3 - written);
            if (c1 == 0) break;
            Array.Copy(output, ra, result, written, c1);
            written += c1;
            ra += c1;
            int c2 = Math.Min(chunk, n3 - written);
            if (c2 == 0) break;
            Array.Copy(output, rb, result, written, c2);
            written += c2;
            rb += c2;
        }
        return result;
    }

    private static long Key(int count, int vertex) => (long)count << 32 | (uint)vertex;

    /// <summary>Per-triangle liveness and per-vertex live triangle counts while emitting.</summary>
    private sealed class FanState(int triCount, int vertexCount)
    {
        public readonly bool[] Live = new bool[triCount];
        public readonly int[] Count = new int[vertexCount];
        public readonly PriorityQueue<int, long> Fallback = new();
        public int Active;
        public int[] List = [];
    }

    /// <summary>Returns the index list with each triangle's repeated vertices replaced by -1.</summary>
    private static int[] BuildAdjacency(uint[] idx, int triCount, int[] adj, int[] offsets)
    {
        int n3 = 3 * triCount;
        var clean = new int[n3];
        for (int t = 0; t < triCount; t++)
        {
            int a = (int)idx[3 * t], b = (int)idx[3 * t + 1], c = (int)idx[3 * t + 2];
            if (b == a) b = -1;
            if (c == a || c == b) c = -1;
            clean[3 * t] = a; clean[3 * t + 1] = b; clean[3 * t + 2] = c;
        }
        for (int i = 0; i < n3; i++)
            if (clean[i] != -1) offsets[clean[i] + 1]++;
        for (int i = 1; i < offsets.Length; i++)
            offsets[i] += offsets[i - 1];
        // Each vertex's triangles in index-list order (the exe scans for the next free slot).
        var fill = offsets[..^1];
        for (int i = 0; i < n3; i++)
        {
            int v = clean[i];
            if (v != -1)
                adj[fill[v]++] = i / 3;
        }
        return clean;
    }

    /// <summary>
    /// Emits (or, without <paramref name="output"/>, simulates) all live triangles of one vertex (0x14021EF40):
    /// repeatedly the triangle with the fewest cache misses (first in the swap-removed list on ties), pushing missing
    /// vertices into the FIFO. Returns the total misses.
    /// </summary>
    private static int EmitFan(int[] buf, ref int start, ref int end, FanState fan, int[] adj, int s, int e, uint[] idx,
        int[] clean, uint[]? output, ref int outPos)
    {
        var list = fan.List;
        var live = fan.Live;
        int n = 0;
        for (int k = s; k < e; k++)
            if (live[adj[k]]) list[n++] = adj[k];
        int total = 0;
        while (n > 0)
        {
            int bestTri = -1, at = -1;
            uint bestMiss = uint.MaxValue;
            for (int i = 0; i < n; i++)
            {
                int t = list[i];
                int a = (int)idx[3 * t], b = (int)idx[3 * t + 1], c = (int)idx[3 * t + 2];
                bool fa = false, fb = false, fc = false;
                for (int p = start; p <= end; p++)
                {
                    int x = buf[p];
                    if (x == a) fa = true;
                    if (x == b) fb = true;
                    if (x == c) fc = true;
                }
                uint miss = (fa ? 0u : 1u) + (fb ? 0u : 1u) + (fc ? 0u : 1u);
                if (miss < bestMiss)
                {
                    bestTri = t;
                    at = i;
                    bestMiss = miss;
                }
            }
            int ta = (int)idx[3 * bestTri], tb = (int)idx[3 * bestTri + 1], tc = (int)idx[3 * bestTri + 2];
            total += Push(buf, ref start, ref end, ta, tb, tc);
            if (output is not null)
            {
                live[bestTri] = false;
                output[outPos++] = (uint)ta;
                output[outPos++] = (uint)tb;
                output[outPos++] = (uint)tc;
                for (int k = 3 * bestTri; k < 3 * bestTri + 3; k++)
                    if (clean[k] != -1)
                        Retire(fan, clean[k]);
            }
            n--;
            list[at] = list[n];
        }
        return total;
    }

    private static void Retire(FanState fan, int v)
    {
        int c = --fan.Count[v];
        if (c == 0)
            fan.Active--;
        else
            fan.Fallback.Enqueue(v, Key(c, v));
    }

    /// <summary>FIFO push of a triangle's missing vertices (0x14021EE00); the window slides by one per miss.</summary>
    private static int Push(int[] buf, ref int start, ref int end, int a, int b, int c)
    {
        bool fa = false, fb = false, fc = false;
        for (int p = start; p <= end; p++)
        {
            int x = buf[p];
            if (x == a) fa = true;
            if (x == b) fb = true;
            if (x == c) fc = true;
        }
        int added = 0;
        if (!fa) { start++; end++; buf[end] = a; added++; }
        if (!fb) { start++; end++; buf[end] = b; added++; }
        if (!fc) { start++; end++; buf[end] = c; added++; }
        return added;
    }
}
