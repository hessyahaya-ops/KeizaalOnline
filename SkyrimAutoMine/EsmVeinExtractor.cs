// Extracts ore vein placements (and map markers) from Skyrim.esm.
// Single linear pass: ACTI definitions appear before CELL/WRLD groups,
// so the ore base-form set is complete by the time REFRs show up.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

public static class EsmVeinExtractor
{
    public static string Run(string esmPath, string outPath)
    {
        byte[] b = File.ReadAllBytes(esmPath);
        Dictionary<uint, string> ore = new Dictionary<uint, string>();
        string curWrld = "";
        string curCell = "";
        uint curCellId = 0;
        int veins = 0, markers = 0;
        StringBuilder log = new StringBuilder();

        using (StreamWriter sw = new StreamWriter(outPath))
        {
            sw.WriteLine("kind,formid,baseid,baseEdid,x,y,z,wrld,cellEdid,cellId,fullId");
            long i = 0;
            long n = b.LongLength;
            while (i + 24 <= n)
            {
                string t = Encoding.ASCII.GetString(b, (int)i, 4);
                uint sz = BitConverter.ToUInt32(b, (int)i + 4);
                if (t == "GRUP") { i += 24; continue; } // descend into group

                uint flags = BitConverter.ToUInt32(b, (int)i + 8);
                uint formid = BitConverter.ToUInt32(b, (int)i + 12);
                long dataStart = i + 24;
                long next = dataStart + sz;

                if (t == "ACTI" || t == "REFR" || t == "CELL" || t == "WRLD")
                {
                    byte[] data;
                    if ((flags & 0x00040000u) != 0)
                    {
                        int dsz = BitConverter.ToInt32(b, (int)dataStart);
                        data = new byte[dsz];
                        // skip 4-byte decomp size + 2-byte zlib header
                        using (MemoryStream ms = new MemoryStream(b, (int)dataStart + 6, (int)sz - 6))
                        using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                        {
                            int off = 0, r;
                            while (off < dsz && (r = ds.Read(data, off, dsz - off)) > 0) off += r;
                        }
                    }
                    else
                    {
                        data = new byte[sz];
                        Buffer.BlockCopy(b, (int)dataStart, data, 0, (int)sz);
                    }

                    string edid = null; uint baseId = 0; bool haveBase = false;
                    float px = 0, py = 0, pz = 0; bool havePos = false; uint fullId = 0;
                    int j = 0; int ext = 0;
                    while (j + 6 <= data.Length)
                    {
                        string st = Encoding.ASCII.GetString(data, j, 4);
                        int ssz = BitConverter.ToUInt16(data, j + 4);
                        int payload = j + 6;
                        if (st == "XXXX" && ssz == 4)
                        {
                            ext = BitConverter.ToInt32(data, payload);
                            j = payload + ssz; continue;
                        }
                        if (ext > 0) { ssz = ext; ext = 0; }
                        if (payload + ssz > data.Length) break;
                        if (st == "EDID" && ssz > 1) edid = Encoding.ASCII.GetString(data, payload, ssz - 1);
                        else if (st == "NAME" && ssz >= 4) { baseId = BitConverter.ToUInt32(data, payload); haveBase = true; }
                        else if (st == "DATA" && t == "REFR" && ssz >= 12)
                        {
                            px = BitConverter.ToSingle(data, payload);
                            py = BitConverter.ToSingle(data, payload + 4);
                            pz = BitConverter.ToSingle(data, payload + 8);
                            havePos = true;
                        }
                        else if (st == "FULL" && ssz >= 4) fullId = BitConverter.ToUInt32(data, payload);
                        j = payload + ssz;
                    }

                    if (t == "ACTI" && edid != null && edid.StartsWith("MineOre")) ore[formid] = edid;
                    else if (t == "WRLD") curWrld = (edid != null) ? edid : formid.ToString("X8");
                    else if (t == "CELL") { curCell = (edid != null) ? edid : ""; curCellId = formid; }
                    else if (t == "REFR" && haveBase && havePos)
                    {
                        string be;
                        if (ore.TryGetValue(baseId, out be))
                        {
                            sw.WriteLine(string.Format("vein,{0:X8},{1:X8},{2},{3:F1},{4:F1},{5:F1},{6},{7},{8:X8},",
                                formid, baseId, be, px, py, pz, curWrld, curCell, curCellId));
                            veins++;
                        }
                        else if (baseId == 0x10) // MapMarker
                        {
                            sw.WriteLine(string.Format("marker,{0:X8},{1:X8},MapMarker,{2:F1},{3:F1},{4:F1},{5},{6},{7:X8},{8}",
                                formid, baseId, px, py, pz, curWrld, curCell, curCellId, fullId));
                            markers++;
                        }
                    }
                }
                i = next;
            }
        }
        log.AppendFormat("Ore base forms found: {0}\n", ore.Count);
        foreach (KeyValuePair<uint, string> kv in ore)
            log.AppendFormat("  {0:X8} {1}\n", kv.Key, kv.Value);
        log.AppendFormat("Vein refs: {0}, map markers: {1}\n", veins, markers);
        return log.ToString();
    }
}
