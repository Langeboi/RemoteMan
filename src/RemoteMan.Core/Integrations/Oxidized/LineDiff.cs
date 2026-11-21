namespace RemoteMan.Core.Integrations.Oxidized;

public enum DiffKind { Same, Added, Removed }

/// <summary>One line of a diff. Line numbers are 1-based; 0 where the line isn't in that side.</summary>
public readonly record struct DiffLine(DiffKind Kind, string Text, int OldLine, int NewLine);

/// <summary>Line diff for config versions (Myers' algorithm, after trimming the common start and end).</summary>
public static class LineDiff
{
    public static string[] Split(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');

    public static IReadOnlyList<DiffLine> Compare(IReadOnlyList<string> older, IReadOnlyList<string> newer)
    {
        var result = new List<DiffLine>(Math.Max(older.Count, newer.Count) + 8);
        int start = 0;
        while (start < older.Count && start < newer.Count && older[start] == newer[start]) start++;
        int endO = older.Count, endN = newer.Count;
        while (endO > start && endN > start && older[endO - 1] == newer[endN - 1]) { endO--; endN--; }

        for (var i = 0; i < start; i++) result.Add(new(DiffKind.Same, older[i], i + 1, i + 1));
        Myers(older, newer, start, endO, start, endN, result);
        for (int i = endO, j = endN; i < older.Count; i++, j++) result.Add(new(DiffKind.Same, older[i], i + 1, j + 1));
        return result;
    }

    private static void Myers(IReadOnlyList<string> a, IReadOnlyList<string> b, int a0, int a1, int b0, int b1, List<DiffLine> into)
    {
        int n = a1 - a0, m = b1 - b0;
        if (n == 0 && m == 0) return;
        if (n == 0) { for (var j = b0; j < b1; j++) into.Add(new(DiffKind.Added, b[j], 0, j + 1)); return; }
        if (m == 0) { for (var i = a0; i < a1; i++) into.Add(new(DiffKind.Removed, a[i], i + 1, 0)); return; }

        int max = n + m, off = max;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        int dFound = -1;
        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1]) ? v[off + k + 1] : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[a0 + x] == b[b0 + y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m) { dFound = d; break; }
            }
            if (dFound >= 0) break;
        }

        // Walk back through the saved frontiers.
        var ops = new List<DiffLine>();
        int cx = n, cy = m;
        for (var d = dFound; d > 0; d--)
        {
            var pv = trace[d];
            int k = cx - cy;
            int pk = k == -d || (k != d && pv[off + k - 1] < pv[off + k + 1]) ? k + 1 : k - 1;
            int px = pv[off + pk], py = px - pk;
            while (cx > px && cy > py) { cx--; cy--; ops.Add(new(DiffKind.Same, a[a0 + cx], a0 + cx + 1, b0 + cy + 1)); }
            if (cx == px) { cy--; ops.Add(new(DiffKind.Added, b[b0 + cy], 0, b0 + cy + 1)); }
            else { cx--; ops.Add(new(DiffKind.Removed, a[a0 + cx], a0 + cx + 1, 0)); }
        }
        while (cx > 0 && cy > 0) { cx--; cy--; ops.Add(new(DiffKind.Same, a[a0 + cx], a0 + cx + 1, b0 + cy + 1)); }
        ops.Reverse();
        // Show removals before additions inside each changed block, like git.
        for (var i = 0; i < ops.Count;)
        {
            if (ops[i].Kind == DiffKind.Same) { into.Add(ops[i++]); continue; }
            var j = i;
            while (j < ops.Count && ops[j].Kind != DiffKind.Same) j++;
            for (var t = i; t < j; t++) if (ops[t].Kind == DiffKind.Removed) into.Add(ops[t]);
            for (var t = i; t < j; t++) if (ops[t].Kind == DiffKind.Added) into.Add(ops[t]);
            i = j;
        }
    }

    public static (int Added, int Removed) Count(IEnumerable<DiffLine> diff)
    {
        int a = 0, r = 0;
        foreach (var l in diff) { if (l.Kind == DiffKind.Added) a++; else if (l.Kind == DiffKind.Removed) r++; }
        return (a, r);
    }
}
