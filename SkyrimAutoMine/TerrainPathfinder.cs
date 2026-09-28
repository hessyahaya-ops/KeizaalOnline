// Builds a terrain grid (heights + road texture) for part of Tamriel from Skyrim.esm,
// then plans slope-aware, road-preferring A* paths between route waypoints.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class TerrainPathfinder
{
    const int V = 33;          // vertices per cell side
    const double STEP = 128.0; // units between vertices

    // grid covers cells [cx0..cx1] x [cy0..cy1], one node per vertex (edges shared)
    static int cx0, cy0, cx1, cy1, W, H;
    static float[] height;
    static float[] road;
    static bool[] known;

    public static string Run(string esmPath, string routeIn, string routeOut, int minCx, int maxCx, int minCy, int maxCy)
    {
        StringBuilder log = new StringBuilder();
        cx0 = minCx; cx1 = maxCx; cy0 = minCy; cy1 = maxCy;
        W = (cx1 - cx0 + 1) * (V - 1) + 1;
        H = (cy1 - cy0 + 1) * (V - 1) + 1;
        height = new float[W * H]; road = new float[W * H]; known = new bool[W * H];

        int cells = ExtractTerrain(esmPath, log);
        int k = 0; for (int i = 0; i < known.Length; i++) if (known[i]) k++;
        int r = 0; for (int i = 0; i < road.Length; i++) if (road[i] > 0.4f) r++;
        log.AppendFormat("Terrain: {0} cells loaded, grid {1}x{2}, {3:F0}% known, {4} road nodes\n", cells, W, H, 100.0 * k / known.Length, r);

        PlanRoute(routeIn, routeOut, log);
        return log.ToString();
    }

    // ---------------- ESM terrain ----------------
    static int ExtractTerrain(string esmPath, StringBuilder log)
    {
        byte[] b = File.ReadAllBytes(esmPath);
        Dictionary<uint, bool> roadTex = new Dictionary<uint, bool>();
        List<string> roadNames = new List<string>();
        string curWrld = "";
        bool haveCell = false; int cellX = 0, cellY = 0;
        int cells = 0;
        long i = 0, n = b.LongLength;
        while (i + 24 <= n)
        {
            string t = Encoding.ASCII.GetString(b, (int)i, 4);
            uint sz = BitConverter.ToUInt32(b, (int)i + 4);
            if (t == "GRUP") { i += 24; continue; }
            uint flags = BitConverter.ToUInt32(b, (int)i + 8);
            uint formid = BitConverter.ToUInt32(b, (int)i + 12);
            long ds = i + 24, next = ds + sz;

            if (t == "LTEX" || t == "WRLD" || t == "CELL" || (t == "LAND" && curWrld == "Tamriel" && haveCell))
            {
                byte[] data = Payload(b, ds, sz, flags);
                if (t == "LTEX")
                {
                    string ed = FindEdid(data);
                    // Skyrim paints roads as dirt/snow "Path" and "Cobble" landscape textures
                    if (ed != null && System.Text.RegularExpressions.Regex.IsMatch(ed, "road|path|cobble", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    { roadTex[formid] = true; roadNames.Add(ed); }
                }
                else if (t == "WRLD") { string ed = FindEdid(data); curWrld = ed ?? ""; haveCell = false; }
                else if (t == "CELL") { haveCell = ReadXclc(data, out cellX, out cellY); }
                else if (t == "LAND")
                {
                    if (cellX >= cx0 && cellX <= cx1 && cellY >= cy0 && cellY <= cy1)
                    {
                        if (ParseLand(data, cellX, cellY, roadTex)) cells++;
                    }
                }
            }
            i = next;
        }
        log.AppendFormat("Road textures ({0}): {1}\n", roadNames.Count, string.Join(", ", roadNames.ToArray()));
        return cells;
    }

    static byte[] Payload(byte[] b, long ds, uint sz, uint flags)
    {
        if ((flags & 0x00040000u) != 0)
        {
            int dsz = BitConverter.ToInt32(b, (int)ds);
            byte[] data = new byte[dsz];
            using (MemoryStream ms = new MemoryStream(b, (int)ds + 6, (int)sz - 6))
            using (DeflateStream z = new DeflateStream(ms, CompressionMode.Decompress))
            { int off = 0, r; while (off < dsz && (r = z.Read(data, off, dsz - off)) > 0) off += r; }
            return data;
        }
        byte[] d = new byte[sz]; Buffer.BlockCopy(b, (int)ds, d, 0, (int)sz); return d;
    }

    // iterate subrecords: callback-free, returns list of (type, offset, size)
    static List<KeyValuePair<string, int[]>> Subs(byte[] data)
    {
        List<KeyValuePair<string, int[]>> list = new List<KeyValuePair<string, int[]>>();
        int j = 0, ext = 0;
        while (j + 6 <= data.Length)
        {
            string st = Encoding.ASCII.GetString(data, j, 4);
            int ssz = BitConverter.ToUInt16(data, j + 4);
            int p = j + 6;
            if (st == "XXXX" && ssz == 4) { ext = BitConverter.ToInt32(data, p); j = p + 4; continue; }
            if (ext > 0) { ssz = ext; ext = 0; }
            if (p + ssz > data.Length) break;
            list.Add(new KeyValuePair<string, int[]>(st, new int[] { p, ssz }));
            j = p + ssz;
        }
        return list;
    }

    static string FindEdid(byte[] data)
    {
        foreach (var s in Subs(data)) if (s.Key == "EDID" && s.Value[1] > 1) return Encoding.ASCII.GetString(data, s.Value[0], s.Value[1] - 1);
        return null;
    }

    static bool ReadXclc(byte[] data, out int x, out int y)
    {
        x = y = 0;
        foreach (var s in Subs(data))
            if (s.Key == "XCLC" && s.Value[1] >= 8) { x = BitConverter.ToInt32(data, s.Value[0]); y = BitConverter.ToInt32(data, s.Value[0] + 4); return true; }
        return false;
    }

    static bool ParseLand(byte[] data, int cellX, int cellY, Dictionary<uint, bool> roadTex)
    {
        int gx0 = (cellX - cx0) * (V - 1), gy0 = (cellY - cy0) * (V - 1);
        float[] cellRoad = new float[V * V];
        bool gotHeight = false;
        uint lastAtxtTex = 0; int lastAtxtQuad = -1;
        foreach (var s in Subs(data))
        {
            int p = s.Value[0], sz = s.Value[1];
            if (s.Key == "VHGT" && sz >= 4 + V * V)
            {
                float off = BitConverter.ToSingle(data, p);
                float rowStart = off;
                for (int row = 0; row < V; row++)
                {
                    rowStart += (sbyte)data[p + 4 + row * V];
                    float h = rowStart;
                    for (int col = 0; col < V; col++)
                    {
                        if (col > 0) h += (sbyte)data[p + 4 + row * V + col];
                        int gi = (gy0 + row) * W + (gx0 + col);
                        height[gi] = h * 8f; known[gi] = true;
                    }
                }
                gotHeight = true;
            }
            else if (s.Key == "BTXT" && sz >= 8)
            {
                uint tex = BitConverter.ToUInt32(data, p);
                int q = data[p + 4];
                if (roadTex.ContainsKey(tex))
                    for (int yy = 0; yy <= 16; yy++) for (int xx = 0; xx <= 16; xx++)
                        cellRoad[QuadIndex(q, xx, yy)] = 1f;
            }
            else if (s.Key == "ATXT" && sz >= 8)
            {
                lastAtxtTex = BitConverter.ToUInt32(data, p);
                lastAtxtQuad = data[p + 4];
            }
            else if (s.Key == "VTXT" && lastAtxtQuad >= 0)
            {
                bool isRoad = roadTex.ContainsKey(lastAtxtTex);
                for (int e = 0; e + 8 <= sz; e += 8)
                {
                    int pos = BitConverter.ToUInt16(data, p + e);
                    float op = BitConverter.ToSingle(data, p + e + 4);
                    if (pos > 288) continue;
                    int idx = QuadIndex(lastAtxtQuad, pos % 17, pos / 17);
                    if (isRoad) { if (op > cellRoad[idx]) cellRoad[idx] = op; }
                    else if (op > 0.6f) cellRoad[idx] *= (1f - op); // other texture painted over road
                }
                lastAtxtQuad = -1;
            }
        }
        for (int row = 0; row < V; row++)
            for (int col = 0; col < V; col++)
            {
                int gi = (gy0 + row) * W + (gx0 + col);
                if (cellRoad[row * V + col] > road[gi]) road[gi] = cellRoad[row * V + col];
            }
        return gotHeight;
    }

    static int QuadIndex(int q, int x, int y)
    {
        int ox = (q & 1) != 0 ? 16 : 0, oy = (q & 2) != 0 ? 16 : 0;
        return (oy + y) * V + (ox + x);
    }

    // ---------------- A* ----------------
    const double MAX_SLOPE = 1.0;   // rise/run ~45 degrees: treat as unclimbable
    const double ROAD_COST = 0.35;  // walking on road costs a third

    static int Node(double x, double y)
    {
        int gx = (int)Math.Round(x / STEP) - cx0 * (V - 1);
        int gy = (int)Math.Round(y / STEP) - cy0 * (V - 1);
        if (gx < 0 || gy < 0 || gx >= W || gy >= H) return -1;
        return gy * W + gx;
    }
    static double NX(int node) { return (node % W + cx0 * (V - 1)) * STEP; }
    static double NY(int node) { return (node / W + cy0 * (V - 1)) * STEP; }

    static List<int> AStar(int start, int goal)
    {
        int N = W * H;
        double[] g = new double[N];
        int[] from = new int[N];
        bool[] closed = new bool[N];
        for (int i = 0; i < N; i++) { g[i] = double.MaxValue; from[i] = -1; }
        int gxg = goal % W, gyg = goal / W;
        MinHeap open = new MinHeap();
        g[start] = 0; open.Push(start, Heur(start, gxg, gyg));
        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 }, dy = { 0, 0, 1, -1, 1, -1, 1, -1 };
        int expanded = 0;
        while (open.Count > 0)
        {
            int cur = open.Pop();
            if (closed[cur]) continue;
            if (cur == goal) break;
            closed[cur] = true;
            if (++expanded > 3000000) break;
            int cxn = cur % W, cyn = cur / W;
            for (int d = 0; d < 8; d++)
            {
                int nx = cxn + dx[d], ny = cyn + dy[d];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int nb = ny * W + nx;
                if (closed[nb] || !known[nb]) continue;
                double run = (d < 4) ? STEP : STEP * 1.41421356;
                double rise = Math.Abs(height[nb] - height[cur]);
                double slope = rise / run;
                bool nearEnds = (Math.Abs(nx - gxg) <= 3 && Math.Abs(ny - gyg) <= 3);
                if (slope > MAX_SLOPE && !nearEnds) continue;
                double terrain = 1.0 + 4.0 * Math.Max(0, slope - 0.35);
                double rd = Math.Max(road[nb], road[cur]);
                double mult = 1.0 - (1.0 - ROAD_COST) * rd;
                double c = g[cur] + run * terrain * mult;
                if (c < g[nb]) { g[nb] = c; from[nb] = cur; open.Push(nb, c + Heur(nb, gxg, gyg)); }
            }
        }
        if (from[goal] < 0 && goal != start) return null;
        List<int> path = new List<int>();
        for (int c = goal; c >= 0; c = from[c]) { path.Add(c); if (c == start) break; }
        path.Reverse();
        return path;
    }
    static double Heur(int n, int gx, int gy)
    {
        double ddx = (n % W - gx) * STEP, ddy = (n / W - gy) * STEP;
        return Math.Sqrt(ddx * ddx + ddy * ddy) * ROAD_COST;
    }

    class MinHeap
    {
        List<int> items = new List<int>(); List<double> keys = new List<double>();
        public int Count { get { return items.Count; } }
        public void Push(int it, double k)
        {
            items.Add(it); keys.Add(k); int i = items.Count - 1;
            while (i > 0) { int p = (i - 1) / 2; if (keys[p] <= keys[i]) break; Swap(i, p); i = p; }
        }
        public int Pop()
        {
            int top = items[0]; int last = items.Count - 1;
            Swap(0, last); items.RemoveAt(last); keys.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < items.Count && keys[l] < keys[m]) m = l;
                if (r < items.Count && keys[r] < keys[m]) m = r;
                if (m == i) break; Swap(i, m); i = m;
            }
            return top;
        }
        void Swap(int a, int b) { int t = items[a]; items[a] = items[b]; items[b] = t; double k = keys[a]; keys[a] = keys[b]; keys[b] = k; }
    }

    // ---------------- route expansion ----------------
    static void PlanRoute(string routeIn, string routeOut, StringBuilder log)
    {
        string[] lines = File.ReadAllLines(routeIn);
        string[] hdr = lines[0].Replace("\"", "").Split(',');
        int ix = Array.IndexOf(hdr, "x"), iy = Array.IndexOf(hdr, "y"), iz = Array.IndexOf(hdr, "z"), iid = Array.IndexOf(hdr, "formid"), icell = Array.IndexOf(hdr, "cell");
        List<string[]> rows = new List<string[]>();
        for (int i = 1; i < lines.Length; i++) rows.Add(lines[i].Replace("\"", "").Split(','));

        int totalPts = 0, fails = 0; double straight = 0, walked = 0, roadDist = 0;
        using (StreamWriter sw = new StreamWriter(routeOut))
        {
            sw.WriteLine("type,formid,x,y,z,cell");
            for (int i = 0; i < rows.Count; i++)
            {
                string[] c = rows[i];
                double x = P(c[ix]), y = P(c[iy]), z = P(c[iz]);
                if (i > 0)
                {
                    string[] pc = rows[i - 1];
                    double px = P(pc[ix]), py = P(pc[iy]);
                    straight += Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                    int s = Node(px, py), g = Node(x, y);
                    List<int> path = (s >= 0 && g >= 0) ? AStar(s, g) : null;
                    if (path == null) { fails++; log.AppendFormat("  leg {0}->{1}: no path found, will walk straight\n", i, i + 1); }
                    else
                    {
                        double sinceLast = 0; double lastDir = double.NaN;
                        for (int k = 1; k < path.Count - 1; k++)
                        {
                            double ax = NX(path[k - 1]), ay = NY(path[k - 1]), bx = NX(path[k]), by = NY(path[k]);
                            double seg = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                            walked += seg; sinceLast += seg;
                            if (road[path[k]] > 0.4f) roadDist += seg;
                            double dir = Math.Atan2(NX(path[k + 1]) - bx, NY(path[k + 1]) - by);
                            bool turn = !double.IsNaN(lastDir) && Math.Abs(Wrap(dir - lastDir)) > 0.5;
                            if (sinceLast >= 700 || (turn && sinceLast >= 250))
                            {
                                sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "path,,{0:F1},{1:F1},{2:F1},", bx, by, height[path[k]]));
                                totalPts++; sinceLast = 0;
                            }
                            lastDir = dir;
                        }
                    }
                }
                sw.WriteLine(string.Format(CultureInfo.InvariantCulture, "vein,{0},{1:F1},{2:F1},{3:F1},{4}", c[iid], x, y, z, icell >= 0 && icell < c.Length ? c[icell] : ""));
            }
        }
        log.AppendFormat("Route: {0} veins, {1} path points added, {2} legs failed\n", rows.Count, totalPts, fails);
        log.AppendFormat("Distance: straight-line {0:F0}, planned {1:F0} ({2:F0}% on roads)\n", straight, walked, walked > 0 ? 100 * roadDist / walked : 0);
    }
    static double P(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
    static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }
}
