// Standalone replica of the 2D SPH simulation (Circle.cs / FluidSim2D.compute) for measuring behaviour without Unity.
// See README.md in this folder.
using System;
using System.Numerics;
using System.Collections.Generic;

class Sim
{
    public int n; public float radius, gap, h, mass, target, g, cDamp, k, damping, boundsW, boundsH;
    public float predictT = 1f / 120f;
    public string shared = "lague"; public bool jitter = true;
    public float visc = 0; Vector2[] accel;
    public float nearK = 0; float[] nearDens; public float minP = float.NegativeInfinity;
    public float iStr = 0, iRad = 2.5f; public Vector2 iPos; public bool iOn = false; public string iMode = "ours";
    Vector2[] pos, vel, pred; float[] dens;
    int gw, gh; int[] cellStart, cellParticles, cellOf;
    Random rng = new Random(1);

    public void Init()
    {
        pos = new Vector2[n]; vel = new Vector2[n]; pred = new Vector2[n]; dens = new float[n]; accel = new Vector2[n]; nearDens = new float[n];
        cellParticles = new int[n]; cellOf = new int[n];
        float spacing = radius * 2 + gap;
        int cols = (int)MathF.Ceiling(MathF.Sqrt(n)); int rows = (int)MathF.Ceiling((float)n / cols);
        Vector2 origin = new Vector2(-(cols - 1) * spacing * 0.5f, -(rows - 1) * spacing * 0.5f);
        for (int i = 0; i < n; i++)
        {
            Vector2 p = origin + new Vector2(i % cols, i / cols) * spacing;
            if (jitter) { double a = rng.NextDouble() * 2 * Math.PI, rr = Math.Sqrt(rng.NextDouble()); p += new Vector2((float)(Math.Cos(a) * rr), (float)(Math.Sin(a) * rr)) * spacing * 0.1f; }
            pos[i] = p; pred[i] = p;
        }
    }
    float W(float d) { if (d >= h) return 0; float v = MathF.PI * MathF.Pow(h, 4) / 6f; return (h - d) * (h - d) / v; }
    float dW(float d) { if (d >= h) return 0; return (h - d) * 12f / (MathF.PI * MathF.Pow(h, 4)); }
    float P(float d) => MathF.Max(minP, k * (d - target));
    float Wnear(float d) { if (d >= h) return 0; float v = h - d; return v * v * v * 10f / (MathF.PI * MathF.Pow(h, 5)); }
    float dWnear(float d) { if (d >= h) return 0; float v = h - d; return v * v * 30f / (MathF.PI * MathF.Pow(h, 5)); }
    float NearDensity(Vector2 p)
    {
        float d = 0; Cell(p, out int cx, out int cy);
        for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, gh - 1); y++)
        for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, gw - 1); x++)
        { int c = x + y * gw; for (int q = cellStart[c]; q < cellStart[c + 1]; q++) d += mass * Wnear(Vector2.Distance(p, pred[cellParticles[q]])); }
        return d;
    }
    float Wvisc(float d) { if (d >= h) return 0; float v = h * h - d * d; return v * v * v / (MathF.PI * MathF.Pow(h, 8) / 4f); }
    Vector2 ViscosityForce(int i)
    {
        Vector2 f = Vector2.Zero; Cell(pred[i], out int cx, out int cy);
        for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, gh - 1); y++)
        for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, gw - 1); x++)
        { int c = x + y * gw; for (int q = cellStart[c]; q < cellStart[c + 1]; q++) { int j = cellParticles[q]; if (i == j) continue;
            f += (vel[j] - vel[i]) * Wvisc(Vector2.Distance(pred[i], pred[j])); } }
        return f * visc;
    }
    void Cell(Vector2 p, out int cx, out int cy)
    {
        cx = Math.Clamp((int)((p.X + boundsW * 0.5f) / h), 0, gw - 1);
        cy = Math.Clamp((int)((p.Y + boundsH * 0.5f) / h), 0, gh - 1);
    }
    void BuildGrid()
    {
        gw = Math.Max(1, (int)MathF.Ceiling(boundsW / h)); gh = Math.Max(1, (int)MathF.Ceiling(boundsH / h));
        int cells = gw * gh;
        if (cellStart == null || cellStart.Length != cells + 1) cellStart = new int[cells + 1]; else Array.Clear(cellStart);
        for (int i = 0; i < n; i++) { Cell(pred[i], out int cx, out int cy); cellOf[i] = cx + cy * gw; cellStart[cellOf[i]]++; }
        for (int c = 1; c < cells; c++) cellStart[c] += cellStart[c - 1];
        for (int i = n - 1; i >= 0; i--) cellParticles[--cellStart[cellOf[i]]] = i;
        cellStart[cells] = n;
    }
    float Density(Vector2 p)
    {
        float d = 0; Cell(p, out int cx, out int cy);
        for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, gh - 1); y++)
        for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, gw - 1); x++)
        { int c = x + y * gw; for (int q = cellStart[c]; q < cellStart[c + 1]; q++) d += mass * W(Vector2.Distance(p, pred[cellParticles[q]])); }
        return d;
    }
    Vector2 PressureForce(int i)
    {
        Vector2 f = Vector2.Zero; float pi = P(dens[i]); Cell(pred[i], out int cx, out int cy);
        for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, gh - 1); y++)
        for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, gw - 1); x++)
        { int c = x + y * gw; for (int q = cellStart[c]; q < cellStart[c + 1]; q++) {
            int j = cellParticles[q]; if (i == j) continue;
            Vector2 r = pred[i] - pred[j]; float dst = r.Length(); if (dst >= h) continue;
            Vector2 dir = dst > 0.0001f ? r / dst : new Vector2(MathF.Cos(i * 2.39996f), MathF.Sin(i * 2.39996f));
            float sp = shared == "lague" ? (pi + P(dens[j])) / (2f * dens[j]) : (pi + P(dens[j])) / (dens[i] + dens[j]);
            f += mass * sp * dW(dst) * dir;
            if (nearK != 0) f += mass * nearK * (nearDens[i] + nearDens[j]) / (2f * nearDens[j]) * dWnear(dst) * dir; } }
        return f;
    }
    public void Step(float dt)
    {
        for (int i = 0; i < n; i++) { vel[i] += new Vector2(0, -g) * dt; pred[i] = pos[i] + vel[i] * predictT; }
        BuildGrid();
        for (int i = 0; i < n; i++) { dens[i] = Density(pred[i]); if (nearK != 0) nearDens[i] = NearDensity(pred[i]); }
        for (int i = 0; i < n; i++) { accel[i] = PressureForce(i) / dens[i]; if (visc != 0) accel[i] += ViscosityForce(i); if (iOn) accel[i] += Interaction(i); }
        for (int i = 0; i < n; i++) { vel[i] += accel[i] * dt; vel[i] *= MathF.Pow(1f - damping, dt); }
        Vector2 half = new Vector2(boundsW, boundsH) * 0.5f - Vector2.One * radius;
        for (int i = 0; i < n; i++)
        {
            pos[i] += vel[i] * dt;
            if (MathF.Abs(pos[i].X) > half.X) { pos[i].X = half.X * MathF.Sign(pos[i].X); vel[i].X *= -cDamp; }
            if (MathF.Abs(pos[i].Y) > half.Y) { pos[i].Y = half.Y * MathF.Sign(pos[i].Y); vel[i].Y *= -cDamp; }
        }
    }
    Vector2 Interaction(int i)
    {
        Vector2 off = iPos - pos[i]; float sq = off.LengthSquared();
        if (sq >= iRad * iRad) return Vector2.Zero;
        float dst = MathF.Sqrt(sq); Vector2 dir = dst <= 0.0001f ? Vector2.Zero : off / dst; float cT = 1f - dst / iRad;
        Vector2 a = (dir * iStr - vel[i]) * cT;
        if (iMode == "lague") a += new Vector2(0, g) * cT * Math.Clamp(iStr / 10f, 0f, 1f); // cancel gravity inside the ring (Lague's gravityWeight)
        return a;
    }
    public (int inRing, float meanY, float maxY, int aboveSurface) Lift(float surfaceY)
    {
        int c = 0, above = 0; float my = 0, maxY = -99;
        for (int i = 0; i < n; i++) { if (Vector2.DistanceSquared(pos[i], iPos) < iRad * iRad) { c++; my += pos[i].Y; } if (pos[i].Y > surfaceY + 0.2f) above++; maxY = MathF.Max(maxY, pos[i].Y); }
        return (c, c > 0 ? my / c : 0, maxY, above);
    }
    public (float meanNN, int closePairs, float densStd) Spacing(float closeDst)
    {
        float sumNN = 0; int close = 0; float m = 0, m2 = 0; int cnt = 0;
        for (int i = 0; i < n; i++)
        {
            float best = 9; Cell(pos[i], out int cx, out int cy);
            for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, gh - 1); y++)
            for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, gw - 1); x++)
            { int c = x + y * gw; for (int q = cellStart[c]; q < cellStart[c + 1]; q++) { int j = cellParticles[q]; if (j == i) continue;
                float d = Vector2.Distance(pos[i], pos[j]); best = MathF.Min(best, d); if (j > i && d < closeDst) close++; } }
            sumNN += best; if (dens[i] > 0.9f * target) { m += dens[i]; m2 += dens[i] * dens[i]; cnt++; }
        }
        float mean = cnt > 0 ? m / cnt : 0; float std = cnt > 0 ? MathF.Sqrt(MathF.Max(0, m2 / cnt - mean * mean)) : 0;
        return (sumNN / n, close, std);
    }
    // Airborne = density below 0.7*target (and not fully isolated). Anisotropy of the neighbour cloud: ~1 = chain, ~0.2 = round
    public (int airborne, float anisotropy, float meanNeighbours) Airborne()
    {
        int count = 0; float aniso = 0, nb = 0;
        for (int i = 0; i < n; i++)
        {
            if (dens[i] >= 0.7f * target || dens[i] < 16.5f) continue;
            float sxx = 0, syy = 0, sxy = 0; int m = 0; Cell(pos[i], out int cx, out int cy);
            for (int y = Math.Max(cy - 1, 0); y <= Math.Min(cy + 1, gh - 1); y++)
            for (int x = Math.Max(cx - 1, 0); x <= Math.Min(cx + 1, gw - 1); x++)
            { int c = x + y * gw; for (int q = cellStart[c]; q < cellStart[c + 1]; q++) { int j = cellParticles[q]; if (j == i) continue;
                Vector2 r = pos[j] - pos[i]; if (r.LengthSquared() >= h * h) continue; sxx += r.X * r.X; syy += r.Y * r.Y; sxy += r.X * r.Y; m++; } }
            if (m < 2) continue;
            float tr = sxx + syy, det = sxx * syy - sxy * sxy, disc = MathF.Sqrt(MathF.Max(0, tr * tr / 4 - det));
            float l1 = tr / 2 + disc, l2 = tr / 2 - disc;
            aniso += (l1 - l2) / (l1 + l2 + 1e-6f); nb += m; count++;
        }
        return (count, count > 0 ? aniso / count : 0, count > 0 ? nb / count : 0);
    }
    public float SurfaceY() { var ys = new List<float>(); for (int i = 0; i < n; i++) ys.Add(pos[i].Y); ys.Sort(); return ys[(int)(n * 0.98)]; }

    public (float ke, float comY, float maxD, float floorD, float topD) Stats()
    {
        float ke = 0, cy = 0, maxD = 0; var ys = new List<(float y, float d)>();
        for (int i = 0; i < n; i++) { ke += 0.5f * vel[i].LengthSquared(); cy += pos[i].Y; maxD = MathF.Max(maxD, dens[i]); ys.Add((pos[i].Y, dens[i])); }
        ys.Sort((a, b) => a.y.CompareTo(b.y));
        float floorD = 0, topD = 0; int m = n / 20;
        for (int i = 0; i < m; i++) { floorD += ys[i].d; topD += ys[n - 1 - i].d; }
        return (ke / n, cy / n, maxD, floorD / m, topD / m);
    }
}

class Program
{
    static void Main(string[] args)
    {
        var a = new Dictionary<string, string>();
        foreach (var s in args) { var kv = s.Split('='); a[kv[0]] = kv.Length > 1 ? kv[1] : "1"; }
        float F(string key, float def) => a.TryGetValue(key, out var v) ? float.Parse(v) : def;
        var sim = new Sim
        {
            n = (int)F("n", 4032), radius = F("radius", 0.05f), gap = F("gap", 0.02f), h = F("h", 0.35f), mass = 1,
            target = F("target", 5.2f), g = F("g", 10f), cDamp = F("cdamp", 0.95f), k = F("k", 27.44f),
            damping = F("damping", 0.1f), boundsW = F("bw", 17.78f), boundsH = F("bh", 10f),
            predictT = F("predict", 1f / 120f), shared = a.GetValueOrDefault("shared", "lague"), jitter = F("jitter", 1) > 0
        };
        float fps = F("fps", 60), sub = F("sub", 3), seconds = F("sec", 12);
        sim.visc = F("visc", 0); sim.nearK = F("neark", 0); sim.minP = a.ContainsKey("minp") ? F("minp", 0) : float.NegativeInfinity;
        sim.iStr = F("istr", 0); sim.iRad = F("irad", 2.5f); sim.iMode = a.GetValueOrDefault("imode", "ours");
        float iStart = F("istart", 4), iAbove = F("iabove", 1.0f); // start pulling at iStart seconds, cursor iAbove units above the surface
        float iHold = F("ihold", 1.5f), iDrag = F("idrag", 0f), iTop = F("itop", 3.5f), iRelease = F("irelease", 999f), airFrom = F("airfrom", 0.5f), airTo = F("airto", 3f); // after iHold s, move cursor up at iDrag units/s until y = iTop
        sim.Init();
        float dt = 1f / fps / sub; int frames = (int)(seconds * fps);
        Console.WriteLine($"nearK={sim.nearK} visc={sim.visc} shared={sim.shared} jitter={sim.jitter} k={sim.k} g={sim.g} target={sim.target} h={sim.h} damping={sim.damping} bounds={sim.boundsW}x{sim.boundsH} fps={fps} sub={sub}");
        Console.WriteLine("  t     KE/particle  comY    maxDens  floorDens  topDens");
        float prevCom = 0, prevV = 0; int bounces = 0;
        for (int f = 0; f <= frames; f++)
        {
            if (f % (int)(fps / 2) == 0) { var s = sim.Stats(); Console.WriteLine($"{f / fps,5:F1}  {s.ke,10:F4}  {s.comY,6:F3}  {s.maxD,7:F1}  {s.floorD,8:F1}  {s.topD,7:F1}"); }
            if (sim.iStr != 0 && !sim.iOn && f / fps >= iStart) { float sy = sim.SurfaceY(); sim.iPos = new Vector2(0, sy + iAbove); sim.iOn = true; Console.WriteLine($"-- pull on at t={f / fps:F1}: surface y={sy:F2}, cursor y={sim.iPos.Y:F2}, strength {sim.iStr}, radius {sim.iRad}, mode {sim.iMode}"); }
            if (sim.iOn && f / fps >= iStart + iHold && sim.iPos.Y < iTop) sim.iPos.Y += iDrag / fps;
            if (sim.iOn && f / fps >= iRelease) { sim.iOn = false; Console.WriteLine($"-- released at t={f / fps:F1}"); }
            if (f % (int)(fps / 4) == 0 && f / fps >= airFrom && f / fps <= airTo) { var A = sim.Airborne(); Console.WriteLine($"  air t={f / fps:F2}: airborne {A.airborne,4}  anisotropy {A.anisotropy:F3}  neighbours {A.meanNeighbours:F1}"); }
            if (sim.iOn && f % (int)(fps / 2) == 0 && iRelease > 900) { float sy0 = sim.iPos.Y - iAbove; var L = sim.Lift(sy0); Console.WriteLine($"  t={f / fps,4:F1} cursor y {sim.iPos.Y,5:F2}  in ring {L.inRing,5}  mean y {L.meanY,6:F2}  max y {L.maxY,6:F2}  above surface {L.aboveSurface,5}"); }
            for (int i = 0; i < sub; i++) sim.Step(dt);
            var st = sim.Stats(); float v = st.comY - prevCom; if (f > 1 && MathF.Sign(v) != MathF.Sign(prevV) && MathF.Abs(v) > 1e-4f) bounces++; prevV = v; prevCom = st.comY;
        }
        var sp = sim.Spacing(0.06f);
        Console.WriteLine($"centre-of-mass direction reversals in {seconds}s: {bounces}");
        Console.WriteLine($"spacing: mean nearest-neighbour {sp.meanNN:F4} (rest ~{1f / MathF.Sqrt(sim.target):F3})  pairs closer than 0.06: {sp.closePairs}  interior density std {sp.densStd:F2}");
    }
}
