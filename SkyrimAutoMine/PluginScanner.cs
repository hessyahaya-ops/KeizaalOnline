// Scans a plugin (plus its masters) for interior cells matching a name and lists
// every object reference placed in them, resolving base-object EDIDs across files.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class PluginScanner
{
    // global formid (fileName|localId) -> "TYPE EDID"
    static Dictionary<string, string> baseInfo = new Dictionary<string, string>();

    public static string Run(string dataDir, string plugin, string cellPattern, string outCsv)
    {
        StringBuilder log = new StringBuilder();
        List<string> masters = ReadMasters(Path.Combine(dataDir, plugin));
        log.AppendLine("Masters of " + plugin + ": " + string.Join(", ", masters.ToArray()));

        // index base objects of every master + plugin itself
        List<string> files = new List<string>(masters); files.Add(plugin);
        foreach (string f in files)
        {
            string p = Path.Combine(dataDir, f);
            if (!File.Exists(p)) { log.AppendLine("  missing " + f); continue; }
            List<string> fm = ReadMasters(p);
            IndexBases(p, f, fm);
        }
        log.AppendLine("Indexed base objects: " + baseInfo.Count);

        // walk plugin cells
        string pp = Path.Combine(dataDir, plugin);
        byte[] b = File.ReadAllBytes(pp);
        string curCell = null, curFull = null; uint curCellId = 0; bool inMatch = false;
        List<string> matchedCells = new List<string>();
        int refs = 0;
        using (StreamWriter sw = new StreamWriter(outCsv))
        {
            sw.WriteLine("cellEdid,refId,baseType,baseEdid,x,y,z");
            long i = 0;
            while (i + 24 <= b.LongLength)
            {
                string t = Encoding.ASCII.GetString(b, (int)i, 4);
                uint sz = BitConverter.ToUInt32(b, (int)i + 4);
                if (t == "GRUP") { i += 24; continue; }
                uint flags = BitConverter.ToUInt32(b, (int)i + 8);
                uint fid = BitConverter.ToUInt32(b, (int)i + 12);
                long ds = i + 24;
                if (t == "CELL")
                {
                    byte[] d = Payload(b, ds, sz, flags);
                    curCell = Sub(d, "EDID"); curFull = Sub(d, "FULL"); curCellId = fid;
                    string hay = (curCell ?? "") + " " + (curFull ?? "");
                    inMatch = System.Text.RegularExpressions.Regex.IsMatch(hay, cellPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (inMatch) matchedCells.Add(fid.ToString("X8") + " " + curCell + " \"" + curFull + "\"");
                }
                else if (t == "WRLD") inMatch = false;
                else if (inMatch && (t == "REFR" || t == "ACHR"))
                {
                    byte[] d = Payload(b, ds, sz, flags);
                    uint baseId = 0; float x = 0, y = 0, z = 0;
                    foreach (var s in Subs(d))
                    {
                        if (s.Key == "NAME") baseId = BitConverter.ToUInt32(d, s.Value[0]);
                        else if (s.Key == "DATA" && s.Value[1] >= 12)
                        { x = BitConverter.ToSingle(d, s.Value[0]); y = BitConverter.ToSingle(d, s.Value[0] + 4); z = BitConverter.ToSingle(d, s.Value[0] + 8); }
                    }
                    string info = Resolve(baseId, masters, plugin);
                    string[] parts = info.Split(new char[] { ' ' }, 2);
                    sw.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1:X8},{2},{3},{4:F1},{5:F1},{6:F1}",
                        curCell, fid, parts[0], parts.Length > 1 ? parts[1] : "", x, y, z));
                    refs++;
                }
                i = ds + sz;
            }
        }
        log.AppendLine("Matched cells: " + matchedCells.Count);
        foreach (string c in matchedCells) log.AppendLine("  " + c);
        log.AppendLine("References written: " + refs);
        return log.ToString();
    }

    static string Resolve(uint id, List<string> masters, string self)
    {
        int idx = (int)(id >> 24);
        string file = idx < masters.Count ? masters[idx] : self;
        string v;
        return baseInfo.TryGetValue(file + "|" + (id & 0xFFFFFF).ToString("X6"), out v) ? v : "? " + id.ToString("X8");
    }

    static void IndexBases(string path, string fileName, List<string> fileMasters)
    {
        byte[] b = File.ReadAllBytes(path);
        long i = 0;
        while (i + 24 <= b.LongLength)
        {
            string t = Encoding.ASCII.GetString(b, (int)i, 4);
            uint sz = BitConverter.ToUInt32(b, (int)i + 4);
            if (t == "GRUP")
            {
                // skip cell/world children for speed: base objects live in top-level groups
                int gtype = BitConverter.ToInt32(b, (int)i + 12);
                string label = Encoding.ASCII.GetString(b, (int)i + 8, 4);
                if (gtype == 0 && (label == "CELL" || label == "WRLD" || label == "DIAL" || label == "NAVI" || label == "LAND")) { i += sz; continue; }
                i += 24; continue;
            }
            uint flags = BitConverter.ToUInt32(b, (int)i + 8);
            uint fid = BitConverter.ToUInt32(b, (int)i + 12);
            long ds = i + 24;
            if (t == "ACTI" || t == "STAT" || t == "FURN" || t == "MISC" || t == "CONT" || t == "DOOR" || t == "NPC_" || t == "FLOR" || t == "MSTT")
            {
                byte[] d = Payload(b, ds, sz, flags);
                string ed = Sub(d, "EDID");
                int idx = (int)(fid >> 24);
                string owner = idx < fileMasters.Count ? fileMasters[idx] : fileName;
                baseInfo[owner + "|" + (fid & 0xFFFFFF).ToString("X6")] = t + " " + (ed ?? "");
            }
            i = ds + sz;
        }
    }

    static List<string> ReadMasters(string path)
    {
        List<string> m = new List<string>();
        using (FileStream fs = File.OpenRead(path))
        {
            byte[] h = new byte[24]; fs.Read(h, 0, 24);
            uint sz = BitConverter.ToUInt32(h, 4);
            byte[] d = new byte[sz]; fs.Read(d, 0, (int)sz);
            foreach (var s in Subs(d)) if (s.Key == "MAST") m.Add(Encoding.ASCII.GetString(d, s.Value[0], s.Value[1] - 1));
        }
        return m;
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

    static string Sub(byte[] d, string type)
    {
        foreach (var s in Subs(d))
            if (s.Key == type && s.Value[1] > 1)
            {
                // localized plugins store a 4-byte string ID; show it as hex
                if (type == "FULL" && s.Value[1] == 4 && d[s.Value[0] + 3] < 0x20) return "#" + BitConverter.ToUInt32(d, s.Value[0]).ToString("X");
                return Encoding.ASCII.GetString(d, s.Value[0], s.Value[1] - 1);
            }
        return null;
    }

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
}
