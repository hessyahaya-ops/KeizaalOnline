// Reads the navmesh(es) of one interior cell and plans a walkable route
// door -> veins (nearest-first, by navmesh path length) using A* over triangles.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class NavmeshRoute
{
    static List<float[]> verts = new List<float[]>();
    static List<int[]> tris = new List<int[]>();

    public static string Run(string pluginPath, uint cellLocalId, string stopsCsv, string outCsv)
    {
        StringBuilder log = new StringBuilder();
        LoadNavmesh(pluginPath, cellLocalId, log);
        log.AppendFormat("Navmesh: {0} vertices, {1} triangles\n", verts.Count, tris.Count);
        if (tris.Count == 0) return log.ToString();

        // triangle adjacency via shared edges (merge navmeshes by rounding vertex positions)
        Dictionary<string, List<int>> edges = new Dictionary<string, List<int>>();
        for (int t = 0; t < tris.Count; t++)
            for (int e = 0; e < 3; e++)
            {
                string a = Key(tris[t][e]), b = Key(tris[t][(e + 1) % 3]);
                string k = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                List<int> l; if (!edges.TryGetValue(k, out l)) { l = new List<int>(); edges[k] = l; }
                l.Add(t);
            }
        List<int>[] adj = new List<int>[tris.Count];
        for (int t = 0; t < tris.Count; t++) adj[t] = new List<int>();
        foreach (var l in edges.Values) for (int i = 0; i < l.Count; i++) for (int j = 0; j < l.Count; j++) if (i != j) adj[l[i]].Add(l[j]);
        float[][] cen = new float[tris.Count][];
        for (int t = 0; t < tris.Count; t++)
        {
            float[] a = verts[tris[t][0]], b = verts[tris[t][1]], c = verts[tris[t][2]];
            cen[t] = new float[] { (a[0] + b[0] + c[0]) / 3, (a[1] + b[1] + c[1]) / 3, (a[2] + b[2] + c[2]) / 3 };
        }

        // stops: first row = start (door), others = targets
        string[] lines = File.ReadAllLines(stopsCsv);
        List<string[]> stops = new List<string[]>();
        for (int i = 1; i < lines.Length; i++) stops.Add(lines[i].Split(','));
        int[] stopTri = new int[stops.Count];
        for (int s = 0; s < stops.Count; s++)
        {
            float x = F(stops[s][2]), y = F(stops[s][3]), z = F(stops[s][4]);
            int best = -1; double bd = double.MaxValue;
            for (int t = 0; t < tris.Count; t++)
            {
                double d = Sq(cen[t][0] - x) + Sq(cen[t][1] - y) + 4 * Sq(cen[t][2] - z);
                if (d < bd) { bd = d; best = t; }
            }
            stopTri[s] = best;
            log.AppendFormat("  {0} {1} -> nearest navmesh {2:F0} units away\n", stops[s][0], stops[s][1], Math.Sqrt(bd));
        }

        // greedy nearest-by-path ordering from the door
        List<int> order = new List<int>(); bool[] used = new bool[stops.Count];
        int cur = 0; used[0] = true; order.Add(0);
        List<List<int>> legs = new List<List<int>>();
        for (int n = 1; n < stops.Count; n++)
        {
            int bestS = -1; double bestLen = double.MaxValue; List<int> bestPath = null;
            for (int s = 1; s < stops.Count; s++)
            {
                if (used[s]) continue;
                double len; List<int> p = AStar(stopTri[cur], stopTri[s], adj, cen, out len);
                if (p != null && len < bestLen) { bestLen = len; bestS = s; bestPath = p; }
            }
            if (bestS < 0) { log.AppendLine("  unreachable stops remain"); break; }
            used[bestS] = true; order.Add(bestS); legs.Add(bestPath); cur = bestS;
        }

        using (StreamWriter sw = new StreamWriter(outCsv))
        {
            sw.WriteLine("type,formid,x,y,z,cell");
            string[] d0 = stops[0];
            sw.WriteLine("start," + d0[1] + "," + d0[2] + "," + d0[3] + "," + d0[4] + ",CYRTheBeastsMaw");
            int pts = 0;
            for (int L = 0; L < legs.Count; L++)
            {
                List<int> p = legs[L];
                double since = 0;
                for (int k = 1; k < p.Count - 1; k++)
                {
                    since += Math.Sqrt(Sq(cen[p[k]][0] - cen[p[k - 1]][0]) + Sq(cen[p[k]][1] - cen[p[k - 1]][1]));
                    if (since >= 220)
                    {
                        sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "path,,{0:F1},{1:F1},{2:F1},", cen[p[k]][0], cen[p[k]][1], cen[p[k]][2]));
                        since = 0; pts++;
                    }
                }
                string[] s = stops[order[L + 1]];
                sw.WriteLine(s[0] + "," + s[1] + "," + s[2] + "," + s[3] + "," + s[4] + ",CYRTheBeastsMaw");
            }
            log.AppendFormat("Route: {0} stops, {1} path points\n", order.Count - 1, pts);
            log.Append("Order: ");
            for (int i = 1; i < order.Count; i++) log.Append(stops[order[i]][0] + " " + stops[order[i]][1] + (i < order.Count - 1 ? " -> " : "\n"));
        }
        return log.ToString();
    }

    static string Key(int v) { float[] p = verts[v]; return ((int)Math.Round(p[0] / 4)) + "," + ((int)Math.Round(p[1] / 4)) + "," + ((int)Math.Round(p[2] / 4)); }

    static List<int> AStar(int s, int g, List<int>[] adj, float[][] cen, out double len)
    {
        int N = cen.Length; double[] cost = new double[N]; int[] from = new int[N]; bool[] closed = new bool[N];
        for (int i = 0; i < N; i++) { cost[i] = double.MaxValue; from[i] = -1; }
        SortedSet<Tuple<double, int>> open = new SortedSet<Tuple<double, int>>();
        cost[s] = 0; open.Add(Tuple.Create(D(cen[s], cen[g]), s));
        while (open.Count > 0)
        {
            var top = open.Min; open.Remove(top); int c = top.Item2;
            if (closed[c]) continue; closed[c] = true;
            if (c == g) break;
            foreach (int nb in adj[c])
            {
                double nc = cost[c] + D(cen[c], cen[nb]);
                if (nc < cost[nb]) { cost[nb] = nc; from[nb] = c; open.Add(Tuple.Create(nc + D(cen[nb], cen[g]), nb)); }
            }
        }
        len = cost[g];
        if (s != g && from[g] < 0) return null;
        List<int> path = new List<int>();
        for (int c = g; c >= 0; c = from[c]) { path.Add(c); if (c == s) break; }
        path.Reverse(); return path;
    }

    static void LoadNavmesh(string path, uint cellLocalId, StringBuilder log)
    {
        byte[] b = File.ReadAllBytes(path);
        bool inCell = false; int navms = 0;
        long i = 0;
        while (i + 24 <= b.LongLength)
        {
            string t = Encoding.ASCII.GetString(b, (int)i, 4);
            uint sz = BitConverter.ToUInt32(b, (int)i + 4);
            if (t == "GRUP") { i += 24; continue; }
            uint flags = BitConverter.ToUInt32(b, (int)i + 8);
            uint fid = BitConverter.ToUInt32(b, (int)i + 12);
            long ds = i + 24;
            if (t == "CELL" || t == "WRLD") inCell = (t == "CELL" && (fid & 0xFFFFFF) == cellLocalId);
            else if (t == "NAVM" && inCell)
            {
                byte[] d = Payload(b, ds, sz, flags);
                int j = 0;
                while (j + 6 <= d.Length)
                {
                    string st = Encoding.ASCII.GetString(d, j, 4); int ssz = BitConverter.ToUInt16(d, j + 4); int p = j + 6;
                    if (st == "NVNM") { ParseNvnm(d, p, log); navms++; }
                    j = p + ssz;
                }
            }
            i = ds + sz;
        }
        log.AppendFormat("NAVM records in cell: {0}\n", navms);
    }

    static void ParseNvnm(byte[] d, int p, StringBuilder log)
    {
        // version, crc/unknown, parent worldspace; interior => parent cell formid
        int o = p + 12;
        uint world = BitConverter.ToUInt32(d, p + 8);
        o = p + 16; // parent cell (interior) or grid y/x (exterior) - both 4 bytes
        int nv = BitConverter.ToInt32(d, o); o += 4;
        int baseV = verts.Count;
        for (int v = 0; v < nv; v++, o += 12)
            verts.Add(new float[] { BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8) });
        int nt = BitConverter.ToInt32(d, o); o += 4;
        for (int t = 0; t < nt; t++, o += 16)
        {
            int a = BitConverter.ToInt16(d, o), b2 = BitConverter.ToInt16(d, o + 2), c = BitConverter.ToInt16(d, o + 4);
            if (a >= 0 && a < nv && b2 >= 0 && b2 < nv && c >= 0 && c < nv) tris.Add(new int[] { baseV + a, baseV + b2, baseV + c });
        }
        log.AppendFormat("  navmesh piece: {0} verts, {1} tris (world {2:X8})\n", nv, nt, world);
    }

    static byte[] Payload(byte[] b, long ds, uint sz, uint flags)
    {
        if ((flags & 0x00040000u) != 0)
        {
            int dsz = BitConverter.ToInt32(b, (int)ds); byte[] data = new byte[dsz];
            using (MemoryStream ms = new MemoryStream(b, (int)ds + 6, (int)sz - 6))
            using (DeflateStream z = new DeflateStream(ms, CompressionMode.Decompress))
            { int off = 0, r; while (off < dsz && (r = z.Read(data, off, dsz - off)) > 0) off += r; }
            return data;
        }
        byte[] dd = new byte[sz]; Buffer.BlockCopy(b, (int)ds, dd, 0, (int)sz); return dd;
    }
    static double D(float[] a, float[] b) { return Math.Sqrt(Sq(a[0] - b[0]) + Sq(a[1] - b[1]) + Sq(a[2] - b[2])); }
    static double Sq(double v) { return v * v; }
    static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
}
