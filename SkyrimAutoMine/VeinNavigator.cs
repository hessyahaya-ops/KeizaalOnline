// VeinNavigator - external route-walker for Skyrim SE / Keizaal.
// Reads player position from game memory (found by guided calibration, no
// hardcoded offsets), steers with synthetic keyboard/mouse input, walks a
// waypoint route extracted from Skyrim.esm and activates each ore vein.
//
// Build:  csc /out:VeinNavigator.exe VeinNavigator.cs
// Run:    VeinNavigator.exe route_iron_darkwater.csv
// Keys:   F6 = advance calibration step   F9 = pause/resume   F10 = quit
//
// C# 5 compatible (compiles with the .NET Framework 4 csc shipped in Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class VeinNavigator
{
    // ---------- Win32 ----------
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, uint len);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect; public uint __align;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_SCANCODE = 0x0008, KEYEVENTF_KEYUP = 0x0002;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint PROCESS_VM_READ = 0x0010, PROCESS_QUERY_INFORMATION = 0x0400;
    const uint MEM_COMMIT = 0x1000;
    const uint PAGE_READWRITE = 0x04, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;

    // scancodes
    const ushort SC_W = 0x11, SC_A = 0x1E, SC_S = 0x1F, SC_D = 0x20, SC_E = 0x12, SC_SPACE = 0x39;
    const int VK_F6 = 0x75, VK_F8 = 0x77, VK_F9 = 0x78, VK_F10 = 0x79;

    // ---------- state ----------
    static IntPtr hProc = IntPtr.Zero;
    static int gamePid;
    static IntPtr posAddr = IntPtr.Zero;   // float3 x,y,z
    static IntPtr yawAddr = IntPtr.Zero;   // float, radians (posAddr - 4 on stock layout)
    static IntPtr pitchAddr = IntPtr.Zero; // float (posAddr - 12)
    static double countsPerRadYaw = 0, walkOffset = 0;
    static double countsPerRadPitch = 0;
    static bool wHeld = false;

    class Wp { public string id; public double x, y, z; public string cell; public bool isPath; public string type = "vein"; }

    public static int Main(string[] args)
    {
        Console.WriteLine("=== VeinNavigator ===");
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
        string routePath = args.Length > 0 ? args[0] : "route_iron_darkwater.csv";
        string exeName = args.Length > 1 ? args[1] : "SkyrimSE";
        int startAt = args.Length > 2 ? int.Parse(args[2]) : 1;

        List<Wp> route = LoadRoute(routePath);
        if (route.Count == 0) { Console.WriteLine("No waypoints in " + routePath); return 1; }
        Console.WriteLine("Route: " + route.Count + " waypoints from " + routePath);

        Process[] ps = Process.GetProcessesByName(exeName);
        if (ps.Length == 0) { Console.WriteLine("Game process '" + exeName + "' not running. Start the game first."); return 1; }
        gamePid = ps[0].Id;
        hProc = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, gamePid);
        if (hProc == IntPtr.Zero) { Console.WriteLine("OpenProcess failed (try running this console as the same user as the game)."); return 1; }
        Console.WriteLine("Attached to " + exeName + " (pid " + gamePid + ").");

        if (!Calibrate(route[0])) return 1;

        // main route loop
        Console.WriteLine("\n--- ROUTE START ---  F9 pause/resume, F10 quit.");
        int i = startAt - 1;
        while (i < route.Count)
        {
            if (HandleHotkeys()) { KeyUp(SC_W); return 0; }
            if (!GameForeground()) { KeyUp(SC_W); wHeld = false; Thread.Sleep(300); continue; }

            Wp wp = route[i];
            if (wp.isPath)
            {
                // intermediate terrain/road point: just pass through it
                if (!GoTo(wp)) Console.WriteLine("  couldn't reach path point " + (i + 1) + ", continuing");
                i++;
                continue;
            }
            bool ok = GoTo(wp);
            if (ok && wp.type == "pickup")
            {
                // loose ore on the ground: aim down at it and grab it (there may be a small pile)
                Console.WriteLine("[" + (i + 1) + "/" + route.Count + "] picking up loose ore " + wp.id);
                AimAt(wp, 0);
                for (int t = 0; t < 4; t++) { Tap(SC_E, 60); Thread.Sleep(450); MouseMove((t % 2 == 0) ? 40 : -40, 0, 3); }
            }
            else if (ok)
            {
                Console.WriteLine("[" + (i + 1) + "/" + route.Count + "] arrived at vein " + wp.id + " - mining");
                MineHere(wp);
            }
            else
            {
                Console.WriteLine("[" + (i + 1) + "/" + route.Count + "] SKIPPED " + wp.id + " (stuck/timeout)");
            }
            i++;
        }
        KeyUp(SC_W);
        Console.WriteLine("Route complete.");
        return 0;
    }

    // ---------- calibration ----------
    static bool Calibrate(Wp first)
    {
        Console.WriteLine("\n--- CALIBRATION ---");
        if (first.type == "start")
        {
            Console.WriteLine("Calibration point is the ENTRANCE DOOR of this location:");
            Console.WriteLine("  position X=" + first.x + "  Y=" + first.y + "  Z=" + first.z);
            Console.WriteLine("STEP 1: Stand just inside, right in front of the door you came in through.");
        }
        else
        {
        Console.WriteLine("Waypoint 1 is the first vein of the route:");
        Console.WriteLine("  world position X=" + first.x + "  Y=" + first.y + "  Z=" + first.z);
        Console.WriteLine("STEP 1: In game, walk up to that vein until you are TOUCHING it.");
        }
        Console.WriteLine("        Then press F6 (you can stay in the game window).");
        WaitKey(VK_F6);

        List<IntPtr> cands = null;
        double tol = 250;
        while (tol <= 1000)
        {
            Console.WriteLine("Scanning memory (tolerance " + tol + ")...");
            cands = ScanForPosition(first.x, first.y, first.z, tol);
            Console.WriteLine("  " + cands.Count + " candidate addresses.");
            if (cands.Count > 0 && cands.Count < 200000) break;
            tol *= 2;
        }
        if (cands == null || cands.Count == 0) { Console.WriteLine("No candidates found. Are you at the right vein?"); return false; }

        double[] px = new double[cands.Count], py = new double[cands.Count], pz = new double[cands.Count];
        for (int k = 0; k < cands.Count; k++) { float[] v = ReadF3(cands[k]); px[k] = v[0]; py[k] = v[1]; pz[k] = v[2]; }

        Console.WriteLine("STEP 2: Walk 3-4 steps in any direction, stop, press F6.");
        WaitKey(VK_F6);
        List<IntPtr> moved = new List<IntPtr>();
        for (int k = 0; k < cands.Count; k++)
        {
            float[] v = ReadF3(cands[k]);
            double d = Math.Sqrt(Sq(v[0] - px[k]) + Sq(v[1] - py[k]));
            if (d > 40 && d < 3000 && Math.Abs(v[2] - pz[k]) < 600) moved.Add(cands[k]);
        }
        Console.WriteLine("  " + moved.Count + " candidates moved with you.");
        if (moved.Count == 0) { Console.WriteLine("Calibration failed at step 2."); return false; }

        Console.WriteLine("STEP 3: Now TURN IN PLACE (mouse only, don't move), press F6.");
        float[] yawBefore = new float[moved.Count];
        for (int k = 0; k < moved.Count; k++) yawBefore[k] = ReadF1(Off(moved[k], -4));
        float[] posBefore = new float[moved.Count * 2];
        for (int k = 0; k < moved.Count; k++) { float[] v = ReadF3(moved[k]); posBefore[k * 2] = v[0]; posBefore[k * 2 + 1] = v[1]; }
        WaitKey(VK_F6);
        for (int k = 0; k < moved.Count; k++)
        {
            float[] v = ReadF3(moved[k]);
            double dpos = Math.Sqrt(Sq(v[0] - posBefore[k * 2]) + Sq(v[1] - posBefore[k * 2 + 1]));
            float yawNow = ReadF1(Off(moved[k], -4));
            if (dpos < 80 && InAngleRange(yawNow) && InAngleRange(yawBefore[k]) && AngDiff(yawNow, yawBefore[k]) > 0.15)
            {
                posAddr = moved[k]; yawAddr = Off(moved[k], -4); pitchAddr = Off(moved[k], -12);
                break;
            }
        }
        if (posAddr == IntPtr.Zero)
        {
            posAddr = moved[0];
            Console.WriteLine("WARNING: heading not confirmed; falling back to first moving candidate.");
            yawAddr = Off(posAddr, -4); pitchAddr = Off(posAddr, -12);
        }
        Console.WriteLine("Player position locked: 0x" + posAddr.ToInt64().ToString("X"));
        float[] p0 = ReadF3(posAddr);
        Console.WriteLine("  reads (" + p0[0].ToString("F0") + ", " + p0[1].ToString("F0") + ", " + p0[2].ToString("F0") + "), yaw " + ReadF1(yawAddr).ToString("F2"));

        // mouse yaw calibration
        Console.WriteLine("STEP 4: Click INTO the game window so it has focus. Auto-calibrating mouse turn...");
        Console.WriteLine("        (character will turn on its own for a moment)");
        while (!GameForeground()) Thread.Sleep(200);
        Thread.Sleep(600);
        double y0 = ReadF1(yawAddr);
        MouseMove(600, 0, 12);
        Thread.Sleep(350);
        double y1 = ReadF1(yawAddr);
        double dyaw = WrapPi(y1 - y0);
        if (Math.Abs(dyaw) < 0.02)
        {
            Console.WriteLine("Mouse turn had no effect on yaw. Is the game paused/in a menu?"); return false;
        }
        countsPerRadYaw = 600.0 / dyaw;
        Console.WriteLine("  yaw calibration: " + countsPerRadYaw.ToString("F0") + " counts/radian");

        // pitch calibration (optional)
        double pt0 = ReadF1(pitchAddr);
        MouseMove(0, 300, 8);
        Thread.Sleep(300);
        double pt1 = ReadF1(pitchAddr);
        double dpitch = WrapPi(pt1 - pt0);
        if (Math.Abs(dpitch) > 0.01) { countsPerRadPitch = 300.0 / dpitch; Console.WriteLine("  pitch calibration: " + countsPerRadPitch.ToString("F0") + " counts/radian"); }
        else Console.WriteLine("  pitch not calibrated (aiming will be yaw-only).");
        MouseMove(0, (int)(-dpitch * (countsPerRadPitch == 0 ? 0 : countsPerRadPitch)), 8); // undo

        // walk-direction convention
        Console.WriteLine("STEP 5: Auto-measuring walk direction (character will step forward)...");
        float[] a = ReadF3(posAddr);
        KeyDown(SC_W); Thread.Sleep(700); KeyUp(SC_W); Thread.Sleep(250);
        float[] b = ReadF3(posAddr);
        double mdx = b[0] - a[0], mdy = b[1] - a[1];
        if (Math.Sqrt(mdx * mdx + mdy * mdy) < 30) { Console.WriteLine("Character didn't move - is the game window focused and unpaused?"); return false; }
        double moveBearing = Math.Atan2(mdx, mdy);
        walkOffset = WrapPi(moveBearing - ReadF1(yawAddr));
        Console.WriteLine("  walk offset: " + walkOffset.ToString("F2") + " rad. Calibration complete.");
        return true;
    }

    // ---------- navigation ----------
    static bool GoTo(Wp wp)
    {
        DateTime start = DateTime.UtcNow;
        DateTime lastStuckCheck = DateTime.UtcNow;
        float[] lastPos = ReadF3(posAddr);
        int unstick = 0;
        double bestDist = double.MaxValue; DateTime bestAt = DateTime.UtcNow;

        while (true)
        {
            if (HandleHotkeys()) { Environment.Exit(0); }
            if (!GameForeground()) { if (wHeld) { KeyUp(SC_W); wHeld = false; } Thread.Sleep(250); continue; }
            if ((DateTime.UtcNow - start).TotalSeconds > (wp.isPath ? 30 : 90)) { if (wHeld) { KeyUp(SC_W); wHeld = false; } return false; }

            float[] p = ReadF3(posAddr);
            if (!Sane(p)) { Thread.Sleep(100); continue; }
            double dx = wp.x - p[0], dy = wp.y - p[1];
            double dist = Math.Sqrt(dx * dx + dy * dy);
            // path points: keep W held and flow straight into the next one
            if (wp.isPath && dist < 260) return true;
            if (dist < 170) { if (wHeld) { KeyUp(SC_W); wHeld = false; } return true; }
            // wall veins can sit a few metres off the walkable floor: if we're close and
            // can't get any closer, this is as near as it gets
            if (dist < bestDist - 30) { bestDist = dist; bestAt = DateTime.UtcNow; }
            else if (!wp.isPath && dist < 450 && (DateTime.UtcNow - bestAt).TotalSeconds > 3)
            { if (wHeld) { KeyUp(SC_W); wHeld = false; } return true; }

            double bearing = Math.Atan2(dx, dy);
            double yaw = ReadF1(yawAddr);
            double err = WrapPi(bearing - (yaw + walkOffset));

            // Gentle turn rate so the character (which in third person only rotates
            // while moving) can keep up with the camera.
            int step = (int)Clamp(err * countsPerRadYaw * 0.25, -60, 60);
            if (step != 0) MouseMove(step, 0, 1);

            // Always keep walking: in third person, stopping to turn just spins the camera.
            bool shouldWalk = true;
            if (shouldWalk && !wHeld) { KeyDown(SC_W); wHeld = true; }
            if (!shouldWalk && wHeld) { KeyUp(SC_W); wHeld = false; }

            // stuck detection
            if ((DateTime.UtcNow - lastStuckCheck).TotalSeconds > 2.5)
            {
                double moved = Math.Sqrt(Sq(p[0] - lastPos[0]) + Sq(p[1] - lastPos[1]));
                Console.WriteLine("  dist " + dist.ToString("F0") + "  yaw " + yaw.ToString("F2") +
                    "  heading-error " + err.ToString("F2") + " rad  moved " + moved.ToString("F0"));
                if (wHeld && moved < 60)
                {
                    unstick++;
                    Console.WriteLine("  stuck (" + unstick + ") - trying to free");
                    KeyUp(SC_W); wHeld = false;
                    Tap(SC_SPACE, 80);                       // jump
                    KeyDown(SC_S); Thread.Sleep(500); KeyUp(SC_S);
                    ushort side = (unstick % 2 == 0) ? SC_A : SC_D;
                    KeyDown(side); KeyDown(SC_W); Thread.Sleep(800); KeyUp(side); KeyUp(SC_W);
                    if (unstick >= 5) return false;
                }
                lastPos = p; lastStuckCheck = DateTime.UtcNow;
            }
            Thread.Sleep(33);
        }
    }

    static void MineHere(Wp wp)
    {
        AimAt(wp, 40);
        OpenMinigameAndWait();
    }

    static void AimAt(Wp wp, double zOffset)
    {
        float[] p = ReadF3(posAddr);
        double dx = wp.x - p[0], dy = wp.y - p[1], dz = (wp.z + zOffset) - (p[2] + 90); // target vs eye height
        double bearing = Math.Atan2(dx, dy);
        double yaw = ReadF1(yawAddr);
        int step = (int)((WrapPi(bearing - (yaw + walkOffset))) * countsPerRadYaw);
        MouseMove(step, 0, 10);
        Thread.Sleep(150);
        if (countsPerRadPitch != 0)
        {
            double horiz = Math.Sqrt(dx * dx + dy * dy);
            double wantPitch = -Math.Atan2(dz, horiz);     // Skyrim pitch: +down (sign handled by calibration constant)
            double curPitch = ReadF1(pitchAddr);
            int pstep = (int)Clamp((wantPitch - curPitch) * countsPerRadPitch, -800, 800);
            MouseMove(0, pstep, 10);
            Thread.Sleep(150);
        }
    }

    static void OpenMinigameAndWait()
    {
        // Open Keizaal's mining minigame once, then hand control to the player.
        Tap(SC_E, 60);
        Console.WriteLine("  >>> Minigame time. Mine the vein, then press F8 to go to the next one.");
        Console.WriteLine("      (If the minigame didn't open, aim at the vein and press E yourself.)");
        Console.Beep(880, 150);
        while ((GetAsyncKeyState(VK_F8) & 0x8000) != 0) Thread.Sleep(50);
        while ((GetAsyncKeyState(VK_F8) & 0x8000) == 0)
        {
            if ((GetAsyncKeyState(VK_F10) & 0x8000) != 0) { Console.WriteLine("F10 - quitting."); Environment.Exit(0); }
            Thread.Sleep(50);
        }
        Console.WriteLine("  resuming route.");
        Thread.Sleep(400); // let the minigame window close and focus return
        // re-level the view roughly
        if (countsPerRadPitch != 0)
        {
            double cur = ReadF1(pitchAddr);
            MouseMove(0, (int)Clamp((0 - cur) * countsPerRadPitch, -800, 800), 8);
        }
    }

    // ---------- memory ----------
    static List<IntPtr> ScanForPosition(double x, double y, double z, double tol)
    {
        List<IntPtr> hits = new List<IntPtr>();
        IntPtr addr = IntPtr.Zero;
        MEMORY_BASIC_INFORMATION mbi;
        byte[] buf = new byte[1 << 20];
        long scanned = 0;
        while (VirtualQueryEx(hProc, addr, out mbi, (uint)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) != 0)
        {
            long regionSize = mbi.RegionSize.ToInt64();
            if (mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_READWRITE) != 0 &&
                (mbi.Protect & PAGE_GUARD) == 0 && (mbi.Protect & PAGE_NOACCESS) == 0)
            {
                long baseA = mbi.BaseAddress.ToInt64();
                long off = 0;
                while (off < regionSize)
                {
                    int chunk = (int)Math.Min(buf.Length, regionSize - off);
                    IntPtr got;
                    if (ReadProcessMemory(hProc, new IntPtr(baseA + off), buf, new IntPtr(chunk), out got) && got.ToInt64() >= 12)
                    {
                        int n = (int)got.ToInt64() - 8;
                        for (int i2 = 0; i2 <= n - 4; i2 += 4)
                        {
                            float fx = BitConverter.ToSingle(buf, i2);
                            if (fx > x - tol && fx < x + tol)
                            {
                                float fy = BitConverter.ToSingle(buf, i2 + 4);
                                if (fy > y - tol && fy < y + tol)
                                {
                                    float fz = BitConverter.ToSingle(buf, i2 + 8);
                                    if (fz > z - tol - 100 && fz < z + tol + 100)
                                        hits.Add(new IntPtr(baseA + off + i2));
                                }
                            }
                        }
                    }
                    off += chunk;
                    scanned += chunk;
                }
            }
            long nextA = mbi.BaseAddress.ToInt64() + regionSize;
            if (nextA <= addr.ToInt64()) break;
            addr = new IntPtr(nextA);
            if (nextA > 0x7FFFFFFFFFFF) break;
        }
        Console.WriteLine("  scanned " + (scanned >> 20) + " MB");
        return hits;
    }

    static float[] ReadF3(IntPtr a)
    {
        byte[] b = new byte[12]; IntPtr got;
        ReadProcessMemory(hProc, a, b, new IntPtr(12), out got);
        return new float[] { BitConverter.ToSingle(b, 0), BitConverter.ToSingle(b, 4), BitConverter.ToSingle(b, 8) };
    }
    static float ReadF1(IntPtr a)
    {
        byte[] b = new byte[4]; IntPtr got;
        ReadProcessMemory(hProc, a, b, new IntPtr(4), out got);
        return BitConverter.ToSingle(b, 0);
    }

    // ---------- input ----------
    static void KeyDown(ushort sc) { SendKey(sc, false); }
    static void KeyUp(ushort sc) { SendKey(sc, true); }
    static void Tap(ushort sc, int ms) { KeyDown(sc); Thread.Sleep(ms); KeyUp(sc); }
    static void SendKey(ushort sc, bool up)
    {
        INPUT[] inp = new INPUT[1];
        inp[0].type = INPUT_KEYBOARD;
        inp[0].ki.wScan = sc;
        inp[0].ki.dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
    }
    static void MouseMove(int dx, int dy, int steps)
    {
        if (steps < 1) steps = 1;
        for (int s = 0; s < steps; s++)
        {
            INPUT[] inp = new INPUT[1];
            inp[0].type = INPUT_MOUSE;
            inp[0].mi.dx = dx / steps; inp[0].mi.dy = dy / steps;
            inp[0].mi.dwFlags = MOUSEEVENTF_MOVE;
            SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
            if (steps > 1) Thread.Sleep(12);
        }
    }

    // ---------- helpers ----------
    static bool GameForeground()
    {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        return pid == (uint)gamePid;
    }
    static bool HandleHotkeys()
    {
        if ((GetAsyncKeyState(VK_F10) & 0x8000) != 0) { Console.WriteLine("F10 - quitting."); return true; }
        if ((GetAsyncKeyState(VK_F9) & 0x8000) != 0)
        {
            if (wHeld) { KeyUp(SC_W); wHeld = false; }
            Console.WriteLine("F9 - PAUSED. Press F9 again to resume.");
            Thread.Sleep(600);
            while ((GetAsyncKeyState(VK_F9) & 0x8000) == 0)
            {
                if ((GetAsyncKeyState(VK_F10) & 0x8000) != 0) return true;
                Thread.Sleep(100);
            }
            Console.WriteLine("resumed.");
            Thread.Sleep(600);
        }
        return false;
    }
    static void WaitKey(int vk)
    {
        while ((GetAsyncKeyState(vk) & 0x8000) != 0) Thread.Sleep(50); // wait for release first
        while ((GetAsyncKeyState(vk) & 0x8000) == 0) Thread.Sleep(50);
        Thread.Sleep(150);
    }
    static List<Wp> LoadRoute(string path)
    {
        List<Wp> r = new List<Wp>();
        if (!File.Exists(path)) return r;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return r;
        string[] hdr = lines[0].Replace("\"", "").Split(',');
        int ix = Array.IndexOf(hdr, "x"), iy = Array.IndexOf(hdr, "y"), iz = Array.IndexOf(hdr, "z");
        int iid = Array.IndexOf(hdr, "formid"), icell = Array.IndexOf(hdr, "cell"), itype = Array.IndexOf(hdr, "type");
        for (int i = 1; i < lines.Length; i++)
        {
            string[] c = lines[i].Replace("\"", "").Split(',');
            if (c.Length <= Math.Max(ix, Math.Max(iy, iz))) continue;
            Wp w = new Wp();
            w.id = iid >= 0 ? c[iid] : i.ToString();
            w.x = double.Parse(c[ix], CultureInfo.InvariantCulture);
            w.y = double.Parse(c[iy], CultureInfo.InvariantCulture);
            w.z = double.Parse(c[iz], CultureInfo.InvariantCulture);
            w.cell = icell >= 0 && icell < c.Length ? c[icell] : "";
            if (itype >= 0) w.type = c[itype];
            w.isPath = w.type == "path" || w.type == "start";
            r.Add(w);
        }
        return r;
    }
    static int SelfTest()
    {
        // bearing math sanity: target due +Y from origin => bearing 0; due +X => pi/2
        double b1 = Math.Atan2(0, 100), b2 = Math.Atan2(100, 0), b3 = Math.Atan2(-100, 0);
        Console.WriteLine("bearing +Y=" + b1 + " +X=" + b2 + " -X=" + b3);
        Console.WriteLine("wrap tests: " + WrapPi(3 * Math.PI) + " " + WrapPi(-3 * Math.PI) + " " + WrapPi(0.5));
        Console.WriteLine("INPUT struct size: " + Marshal.SizeOf(typeof(INPUT)) + " (expect 40)");
        Console.WriteLine("selftest ok");
        return 0;
    }
    static IntPtr Off(IntPtr a, long delta) { return new IntPtr(a.ToInt64() + delta); }
    static double Sq(double v) { return v * v; }
    static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
    static double WrapPi(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }
    static double AngDiff(double a, double b) { return Math.Abs(WrapPi(a - b)); }
    static bool InAngleRange(double a) { return a > -7 && a < 7; }
    static bool Sane(float[] p) { return !float.IsNaN(p[0]) && Math.Abs(p[0]) < 500000 && Math.Abs(p[1]) < 500000; }
}
