// =====================================================================================
//  SealantCG.cs  -  NX Open C# journal
//
//  Montajdaki parçalar arası boşluklara atılacak sealant'ın HACİM / AĞIRLIK / CG / atalet
//  değerlerini hesaplar. Bağlantı bölgeleri (menteşe, cıvata, burç ... ve kullanıcının
//  seçtiği parçalar + çevresindeki emniyet payı, keep-out gövdeleri) hesaba katılmaz.
//
//  Çalıştırma : NX > Tools (Araçlar) > Journal > Play  ->  bu dosyayı seçin  (Alt+F8)
//               Montaj DISPLAYED part ve WORK part olmalı.
//  Ayarlar    : Aşağıdaki "Settings" sınıfı. Birimler mm (part inch olsa da iç hesap mm).
//
//  Not: C# 5 uyumlu yazıldı (eski NX sürümlerinin journal derleyicisi de çalıştırabilsin).
//       Geometri çekirdeği NX API'sine bağlı değil; SEALANT_TEST tanımıyla NX'siz test edilir.
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
#if !SEALANT_TEST
using NXOpen;
using NXOpen.UF;
using NXOpen.Assemblies;
#endif

// ------------------------------------------------------------------------------------
//  AYARLAR
// ------------------------------------------------------------------------------------
public class Settings
{
    // --- Boşluk tanımı -------------------------------------------------------------
    public double MinGap = 0.10;        // mm  bundan küçük boşluk = temas (sealant yok)
    public double MaxGap = 3.00;        // mm  bundan büyük açıklık sealant ile doldurulmaz
    public double MaxAngleDeg = 25.0;   // iki yüzey birbirine bu açıdan daha paralelse "karşılıklı"

    // --- Çözünürlük / hız ------------------------------------------------------------
    public double SampleSize = 1.0;     // mm  örnekleme aralığı (küçük = hassas, yavaş)
    public double FacetSize = 8.0;      // mm  facet üçgenlerinin en uzun kenarı (üstü bölünür)
    public double FacetChordTol = 0.02; // mm  facet kiriş toleransı (eğrisel yüzey sadakati)
    public int MaxThreads = 0;          // 0 = tüm çekirdekler

    // --- Malzeme ---------------------------------------------------------------------
    public double DensityGcm3 = 1.55;   // g/cm3  -> kullanılan sealant'ın TDS değerini girin

    // --- Hariç tutma (bağlantı bölgeleri) -------------------------------------------
    // Bileşen adı / part adı / gövde adı bu kelimelerden birini içerirse (Türkçe karakterler
    // ASCII'ye çevrilip büyük harfle aranır) o parça "bağlantı elemanı" sayılır: ona olan
    // boşluk hesaplanmaz ve çevresindeki ExcludeMargin mesafesi de sealant'sız kalır.
    public string[] ExcludeKeywords = new string[] {
        "HINGE", "MENTESE", "BOLT", "CIVATA", "SCREW", "VIDA", "NUT", "SOMUN",
        "WASHER", "PUL", "PIN", "BUSH", "BURC", "BEARING", "RULMAN", "RIVET", "PERCIN",
        "FASTENER", "LUG" };
    public string ExcludeAttribute = "SEALANT_EXCLUDE"; // bu attribute'u olan bileşen/part hariç
    public double ExcludeMargin = 10.0;  // mm  hariç parçaların çevresindeki sealant'sız bölge
    public bool AskUserSelection = true; // çalışırken ek hariç parça/gövde seçtir

    // Keep-out gövdeleri: kullanıcı tarafından modellenen basit "yasak hacim" solidleri.
    // İçine düşen sealant sayılmaz (ör. menteşe ekseni etrafına bir silindir çizin).
    public string KeepOutName = "SEAL_KEEPOUT"; // gövde/bileşen adı bunu içeriyorsa
    public int KeepOutLayer = 250;              // ya da bu layer'daysa  (0 = kapalı)

    // --- Çıktılar --------------------------------------------------------------------
    public bool CreateCgPoint = true;    // CG'ye "SEALANT_CG" isimli nokta koy
    public bool WriteCsv = true;         // parça çifti bazında CSV raporu (%TEMP%)
    public bool PrePruneWithNxBox = true;// faceting öncesi NX bounding box ile eleme
}

// ------------------------------------------------------------------------------------
//  Vektör
// ------------------------------------------------------------------------------------
public struct V3
{
    public double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
    public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    public static V3 operator *(V3 a, double s) { return new V3(a.X * s, a.Y * s, a.Z * s); }
    public static double Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
    public static V3 Cross(V3 a, V3 b) { return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X); }
    public double Len() { return Math.Sqrt(X * X + Y * Y + Z * Z); }
    public V3 Unit() { double l = Len(); return l > 1e-300 ? this * (1.0 / l) : new V3(0, 0, 0); }
}

// ------------------------------------------------------------------------------------
//  Dünya koordinatlarında (mm) tüm üçgenler. Struct-of-arrays => cache dostu, paralel okunur.
// ------------------------------------------------------------------------------------
public sealed class TriSoup
{
    public double[] P = new double[9 * 4096];  // a,b,c  (9 double)
    public double[] N = new double[3 * 4096];  // birim dış normal
    public int[] Body = new int[4096];
    public int Count;
    public double MaxEdge;                       // bundan uzun kenarlı üçgenler bölünür

    public TriSoup(double maxEdge) { MaxEdge = maxEdge; }

    public V3 A(int t) { int i = 9 * t; return new V3(P[i], P[i + 1], P[i + 2]); }
    public V3 B(int t) { int i = 9 * t + 3; return new V3(P[i], P[i + 1], P[i + 2]); }
    public V3 C(int t) { int i = 9 * t + 6; return new V3(P[i], P[i + 1], P[i + 2]); }
    public V3 Nrm(int t) { int i = 3 * t; return new V3(N[i], N[i + 1], N[i + 2]); }

    public void Add(V3 a, V3 b, V3 c, V3 n, int body)
    {
        double ab = (b - a).Len(), bc = (c - b).Len(), ca = (a - c).Len();
        double m = Math.Max(ab, Math.Max(bc, ca));
        if (m > MaxEdge)
        {   // en uzun kenarın ortasından ikiye böl (büyük panel üçgenlerini grid'e sığdırır)
            if (m == ab) { V3 h = (a + b) * 0.5; Add(a, h, c, n, body); Add(h, b, c, n, body); }
            else if (m == bc) { V3 h = (b + c) * 0.5; Add(a, b, h, n, body); Add(a, h, c, n, body); }
            else { V3 h = (c + a) * 0.5; Add(a, b, h, n, body); Add(h, b, c, n, body); }
            return;
        }
        if (Count == Body.Length)
        {
            int cap = Body.Length * 2;
            Array.Resize(ref P, 9 * cap); Array.Resize(ref N, 3 * cap); Array.Resize(ref Body, cap);
        }
        int i = 9 * Count;
        P[i] = a.X; P[i + 1] = a.Y; P[i + 2] = a.Z;
        P[i + 3] = b.X; P[i + 4] = b.Y; P[i + 5] = b.Z;
        P[i + 6] = c.X; P[i + 7] = c.Y; P[i + 8] = c.Z;
        N[3 * Count] = n.X; N[3 * Count + 1] = n.Y; N[3 * Count + 2] = n.Z;
        Body[Count] = body;
        Count++;
    }

    public void TriBox(int t, out V3 mn, out V3 mx)
    {
        int i = 9 * t;
        mn = new V3(Math.Min(P[i], Math.Min(P[i + 3], P[i + 6])), Math.Min(P[i + 1], Math.Min(P[i + 4], P[i + 7])), Math.Min(P[i + 2], Math.Min(P[i + 5], P[i + 8])));
        mx = new V3(Math.Max(P[i], Math.Max(P[i + 3], P[i + 6])), Math.Max(P[i + 1], Math.Max(P[i + 4], P[i + 7])), Math.Max(P[i + 2], Math.Max(P[i + 5], P[i + 8])));
    }
}

public enum BodyRole { Normal = 0, Excluded = 1, KeepOut = 2 }

public sealed class BodyInfo
{
    public string Name;              // rapor için (bileşen yolu / gövde)
    public BodyRole Role;
    public int TriStart, TriCount;
    public V3 Min = new V3(double.MaxValue, double.MaxValue, double.MaxValue);
    public V3 Max = new V3(-double.MaxValue, -double.MaxValue, -double.MaxValue);
    public bool Active;              // yakınında (MaxGap içinde) başka gövde var mı
}

// ------------------------------------------------------------------------------------
//  Uniform spatial hash grid  (sıralı anahtar dizisi + sözlük => az bellek, kilitsiz okuma)
// ------------------------------------------------------------------------------------
public sealed class Grid
{
    readonly double h, ox, oy, oz;
    long[] keys; int[] items;
    readonly Dictionary<long, int> cellStart = new Dictionary<long, int>();
    const long M = 1L << 21;

    public Grid(TriSoup s, List<int> tris, double cell, V3 origin)
    {
        h = cell; ox = origin.X - cell; oy = origin.Y - cell; oz = origin.Z - cell;
        // 2 geçiş: önce hücre-üçgen çifti sayısı, sonra doldurma (ara dizi tutmadan, az bellek)
        long total = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int w = 0;
            for (int k = 0; k < tris.Count; k++)
            {
                V3 mn, mx; s.TriBox(tris[k], out mn, out mx);
                int x0 = Ix(mn.X, ox), y0 = Ix(mn.Y, oy), z0 = Ix(mn.Z, oz), x1 = Ix(mx.X, ox), y1 = Ix(mx.Y, oy), z1 = Ix(mx.Z, oz);
                if (pass == 0) { total += (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1); continue; }
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++) { keys[w] = Key(x, y, z); items[w] = tris[k]; w++; }
            }
            if (pass == 0) { keys = new long[total]; items = new int[total]; }
        }
        Array.Sort(keys, items);
        for (long i = 0; i < total; i++) if (i == 0 || keys[i] != keys[i - 1]) cellStart[keys[i]] = (int)i;
    }

    int Ix(double v, double o) { int i = (int)Math.Floor((v - o) / h); return i < 0 ? 0 : (i >= M ? (int)M - 1 : i); }
    static long Key(int x, int y, int z) { return ((long)x * M + y) * M + z; }

    /// mn..mx kutusuyla kesişen hücrelerdeki üçgenleri 'into' kümesine ekler.
    public void Query(V3 mn, V3 mx, HashSet<int> into)
    {
        int x0 = Ix(mn.X, ox), y0 = Ix(mn.Y, oy), z0 = Ix(mn.Z, oz), x1 = Ix(mx.X, ox), y1 = Ix(mx.Y, oy), z1 = Ix(mx.Z, oz);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = Key(x, y, z); int st;
                    if (!cellStart.TryGetValue(key, out st)) continue;
                    for (int i = st; i < keys.Length && keys[i] == key; i++) into.Add(items[i]);
                }
    }
}

// ------------------------------------------------------------------------------------
//  Sonuçlar
// ------------------------------------------------------------------------------------
public sealed class Acc
{
    public double Area, Vol, Sx, Sy, Sz, Sxx, Syy, Szz, Sxy, Sxz, Syz; public long Samples;
    public void AddPoint(double dA, double dV, V3 c)
    {
        Area += dA; Vol += dV; Samples++;
        Sx += dV * c.X; Sy += dV * c.Y; Sz += dV * c.Z;
        Sxx += dV * c.X * c.X; Syy += dV * c.Y * c.Y; Szz += dV * c.Z * c.Z;
        Sxy += dV * c.X * c.Y; Sxz += dV * c.X * c.Z; Syz += dV * c.Y * c.Z;
    }
    public void Merge(Acc o)
    {
        Area += o.Area; Vol += o.Vol; Samples += o.Samples; Sx += o.Sx; Sy += o.Sy; Sz += o.Sz;
        Sxx += o.Sxx; Syy += o.Syy; Szz += o.Szz; Sxy += o.Sxy; Sxz += o.Sxz; Syz += o.Syz;
    }
    public V3 Cg() { return Vol > 0 ? new V3(Sx / Vol, Sy / Vol, Sz / Vol) : new V3(0, 0, 0); }
}

public sealed class SealantResult
{
    public Acc Total = new Acc();
    public Dictionary<long, Acc> Pairs = new Dictionary<long, Acc>();   // (A<<32)|B
    public double DensityGcm3;
    public int Bodies, ActiveBodies, Triangles, CandidateTriangles;
    public double MassKg { get { return Total.Vol * DensityGcm3 * 1e-6; } }  // mm3 * g/cm3 -> kg
    /// CG etrafında atalet (kg*mm2): Ixx, Iyy, Izz, Ixy, Ixz, Iyz  (Ixy = -∫xy dm konvansiyonu)
    public double[] InertiaAtCg()
    {
        Acc t = Total; double r = DensityGcm3 * 1e-6; V3 c = t.Cg(); double m = t.Vol;
        double xx = t.Sxx - m * c.X * c.X, yy = t.Syy - m * c.Y * c.Y, zz = t.Szz - m * c.Z * c.Z;
        double xy = t.Sxy - m * c.X * c.Y, xz = t.Sxz - m * c.X * c.Z, yz = t.Syz - m * c.Y * c.Z;
        return new double[] { (yy + zz) * r, (xx + zz) * r, (xx + yy) * r, -xy * r, -xz * r, -yz * r };
    }
}

// ------------------------------------------------------------------------------------
//  ÇEKİRDEK ALGORİTMA (NX'ten bağımsız, çok çekirdekli)
// ------------------------------------------------------------------------------------
public static class SealantSolver
{
    const double Eps = 1e-3;   // mm, ışın başlangıç ofseti

    public static SealantResult Run(TriSoup s, List<BodyInfo> bodies, Settings cfg)
    {
        var res = new SealantResult();
        res.DensityGcm3 = cfg.DensityGcm3; res.Bodies = bodies.Count; res.Triangles = s.Count;
        double g = cfg.MaxGap, margin = Math.Max(0, cfg.ExcludeMargin);
        double cosTol = Math.Cos(cfg.MaxAngleDeg * Math.PI / 180.0);

        // ---- 1) Gövde seviyesinde geniş faz: sweep-and-prune (X ekseninde sıralı) --------
        int nb = bodies.Count;
        int[] order = new int[nb];
        for (int i = 0; i < nb; i++) order[i] = i;
        double[] minx = new double[nb];
        for (int i = 0; i < nb; i++) minx[i] = bodies[i].Min.X;
        Array.Sort(minx, order);
        var neighbors = new List<int>[nb];
        for (int i = 0; i < nb; i++) neighbors[i] = new List<int>();
        for (int ii = 0; ii < nb; ii++)
        {
            int a = order[ii]; BodyInfo A = bodies[a];
            if (A.Role == BodyRole.KeepOut || A.TriCount == 0) continue;
            for (int jj = ii + 1; jj < nb; jj++)
            {
                int b = order[jj]; BodyInfo B = bodies[b];
                if (B.Min.X > A.Max.X + g) break;
                if (B.Role == BodyRole.KeepOut || B.TriCount == 0) continue;
                if (A.Role == BodyRole.Excluded && B.Role == BodyRole.Excluded) continue;
                if (!BoxOverlap(A.Min, A.Max, B.Min, B.Max, g)) continue;
                neighbors[a].Add(b); neighbors[b].Add(a);
            }
        }
        // aktif: en az bir Normal komşusu olan Normal gövde; Excluded'lar da ışın hedefi olarak lazım
        for (int i = 0; i < nb; i++)
        {
            BodyInfo A = bodies[i];
            if (A.Role == BodyRole.KeepOut) continue;
            foreach (int b in neighbors[i])
                if (A.Role == BodyRole.Normal && bodies[b].Role == BodyRole.Normal) { A.Active = true; break; }
        }
        for (int i = 0; i < nb; i++)
            if (bodies[i].Role == BodyRole.Excluded)
                foreach (int b in neighbors[i]) if (bodies[b].Active) { bodies[i].Active = true; break; }

        // ---- 2) Üçgen seviyesinde grid'ler -------------------------------------------------
        var gridTris = new List<int>(); var candTris = new List<int>(); var exclTris = new List<int>();
        V3 gmin = new V3(double.MaxValue, double.MaxValue, double.MaxValue);
        foreach (BodyInfo B in bodies)
        {
            gmin = new V3(Math.Min(gmin.X, B.Min.X), Math.Min(gmin.Y, B.Min.Y), Math.Min(gmin.Z, B.Min.Z));
            if (B.Role == BodyRole.Excluded) for (int t = B.TriStart; t < B.TriStart + B.TriCount; t++) exclTris.Add(t);
        }
        for (int i = 0; i < nb; i++)
        {
            BodyInfo B = bodies[i];
            if (!B.Active) continue; res.ActiveBodies += B.Role == BodyRole.Normal ? 1 : 0;
            for (int t = B.TriStart; t < B.TriStart + B.TriCount; t++)
            {
                gridTris.Add(t);
                if (B.Role != BodyRole.Normal) continue;
                // üçgen, kendinden büyük indeksli bir Normal komşunun kutusuna MaxGap içinde mi?
                V3 mn, mx; s.TriBox(t, out mn, out mx);
                foreach (int nbI in neighbors[i])
                {
                    BodyInfo O = bodies[nbI];
                    if (nbI > i && O.Role == BodyRole.Normal && BoxOverlap(mn, mx, O.Min, O.Max, g)) { candTris.Add(t); break; }
                }
            }
        }
        res.CandidateTriangles = candTris.Count;
        if (candTris.Count == 0) return res;

        double cell = Math.Max(s.MaxEdge, 2 * g);
        var grid = new Grid(s, gridTris, cell, gmin);
        Grid exGrid = (margin > 0 && exclTris.Count > 0) ? new Grid(s, exclTris, Math.Max(s.MaxEdge, margin), gmin) : null;
        var keepOuts = new List<BodyInfo>();
        foreach (BodyInfo B in bodies) if (B.Role == BodyRole.KeepOut && B.TriCount > 0) keepOuts.Add(B);

        // ---- 3) Dar faz: paralel örnekleme + ışın atma ------------------------------------
        var po = new ParallelOptions();
        po.MaxDegreeOfParallelism = cfg.MaxThreads > 0 ? cfg.MaxThreads : Environment.ProcessorCount;
        object gate = new object();
        BodyRole[] role = new BodyRole[nb];
        for (int i = 0; i < nb; i++) role[i] = bodies[i].Role;

        Parallel.For(0, candTris.Count, po,
            () => new Worker(),
            (ci, state, w) =>
            {
                ProcessTriangle(candTris[ci], s, role, grid, exGrid, keepOuts, cfg, cosTol, margin, w);
                return w;
            },
            w =>
            {
                lock (gate)
                {
                    res.Total.Merge(w.Total);
                    foreach (var kv in w.Pairs)
                    {
                        Acc a; if (!res.Pairs.TryGetValue(kv.Key, out a)) { a = new Acc(); res.Pairs[kv.Key] = a; }
                        a.Merge(kv.Value);
                    }
                }
            });
        return res;
    }

    sealed class Worker
    {
        public Acc Total = new Acc();
        public Dictionary<long, Acc> Pairs = new Dictionary<long, Acc>();
        public HashSet<int> Set = new HashSet<int>();
        public List<int> Hits = new List<int>();
        public List<int> Excl = new List<int>();
        public List<BodyInfo> Ko = new List<BodyInfo>();
    }

    static void ProcessTriangle(int t, TriSoup s, BodyRole[] role, Grid grid, Grid exGrid, List<BodyInfo> keepOuts,
                                Settings cfg, double cosTol, double margin, Worker w)
    {
        int A = s.Body[t];
        V3 a = s.A(t), b = s.B(t), c = s.C(t), n = s.Nrm(t);
        double g = cfg.MaxGap;
        V3 mn, mx; s.TriBox(t, out mn, out mx);
        V3 pad = new V3(g + Eps, g + Eps, g + Eps);

        // Aday hedef üçgenler: kutu sorgusu + "karşılıklı mı" + "önümde mi" filtreleri
        w.Set.Clear(); w.Hits.Clear();
        grid.Query(mn - pad, mx + pad, w.Set);
        bool anyHigher = false;
        double planeD = V3.Dot(n, a);
        foreach (int k in w.Set)
        {
            int bk = s.Body[k];
            if (bk == A) continue;
            if (V3.Dot(n, s.Nrm(k)) > -cosTol) continue;                 // karşılıklı değil
            double da = V3.Dot(n, s.A(k)) - planeD, db = V3.Dot(n, s.B(k)) - planeD, dc = V3.Dot(n, s.C(k)) - planeD;
            if (Math.Max(da, Math.Max(db, dc)) < -Eps) continue;          // tamamen arkamda
            if (Math.Min(da, Math.Min(db, dc)) > g + Eps) continue;       // MaxGap'ten uzak
            w.Hits.Add(k);
            if (bk > A && role[bk] == BodyRole.Normal) anyHigher = true;
        }
        if (!anyHigher || w.Hits.Count == 0) return;

        // Bu üçgen için ilgili hariç-tutma üçgenleri ve keep-out gövdeleri (çoğu zaman boş)
        w.Excl.Clear(); w.Ko.Clear();
        if (exGrid != null)
        {
            V3 pm = new V3(g + margin, g + margin, g + margin);
            w.Set.Clear(); exGrid.Query(mn - pm, mx + pm, w.Set);
            foreach (int k in w.Set) w.Excl.Add(k);
        }
        foreach (BodyInfo K in keepOuts) if (BoxOverlap(mn, mx, K.Min, K.Max, g)) w.Ko.Add(K);

        // Üçgeni m x m alt üçgene böl (barisentrik grid), her alt üçgen merkezinden ışın at.
        // m, örnek başına düşen alan ~ SampleSize^2 olacak şekilde seçilir.
        double L = Math.Max((b - a).Len(), Math.Max((c - b).Len(), (a - c).Len()));
        int m = Math.Max(1, Math.Min(256, (int)Math.Ceiling(L / (cfg.SampleSize * 1.4142))));
        double area = 0.5 * V3.Cross(b - a, c - a).Len();
        double dA = area / ((double)m * m);
        V3 e1 = (b - a) * (1.0 / m), e2 = (c - a) * (1.0 / m);
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m - i; j++)
            {
                Sample(a + e1 * (i + 1.0 / 3) + e2 * (j + 1.0 / 3), n, dA, A, s, role, cfg, margin, w);   // "yukarı" üçgen
                if (i + j < m - 1)
                    Sample(a + e1 * (i + 2.0 / 3) + e2 * (j + 2.0 / 3), n, dA, A, s, role, cfg, margin, w); // "aşağı" üçgen
            }
    }

    static void Sample(V3 p, V3 n, double dA, int A, TriSoup s, BodyRole[] role, Settings cfg, double margin, Worker w)
    {
        V3 o = p - n * Eps;
        double best = double.MaxValue; int bestBody = -1;
        foreach (int k in w.Hits)
        {
            double t;
            if (RayTri(o, n, s.A(k), s.B(k), s.C(k), out t) && t >= 0 && t < best) { best = t; bestBody = s.Body[k]; }
        }
        if (bestBody < 0) return;
        double gap = best - Eps;
        if (gap > cfg.MaxGap) return;            // açıklık çok büyük
        if (gap < cfg.MinGap) return;            // temas: sealant yok
        if (bestBody < A) return;                // çift sayımı önle: hacmi küçük indeksli gövde sayar
        if (role[bestBody] != BodyRole.Normal) return;   // menteşe/cıvata ile arasındaki boşluk

        V3 mid = p + n * (0.5 * gap);
        if (w.Excl.Count > 0)
        {
            double m2 = margin * margin;
            foreach (int k in w.Excl) if (DistSq(mid, s.A(k), s.B(k), s.C(k)) < m2) return;
        }
        foreach (BodyInfo K in w.Ko) if (Inside(mid, K, s)) return;

        double dV = dA * gap;
        w.Total.AddPoint(dA, dV, mid);
        long key = ((long)A << 32) | (uint)bestBody;
        Acc pa; if (!w.Pairs.TryGetValue(key, out pa)) { pa = new Acc(); w.Pairs[key] = pa; }
        pa.AddPoint(dA, dV, mid);
    }

    // ---- geometri yardımcıları -----------------------------------------------------------
    public static bool BoxOverlap(V3 amin, V3 amax, V3 bmin, V3 bmax, double pad)
    {
        return amin.X <= bmax.X + pad && bmin.X <= amax.X + pad &&
               amin.Y <= bmax.Y + pad && bmin.Y <= amax.Y + pad &&
               amin.Z <= bmax.Z + pad && bmin.Z <= amax.Z + pad;
    }

    /// Möller–Trumbore ışın/üçgen kesişimi
    public static bool RayTri(V3 o, V3 d, V3 a, V3 b, V3 c, out double t)
    {
        t = 0;
        V3 e1 = b - a, e2 = c - a, pv = V3.Cross(d, e2);
        double det = V3.Dot(e1, pv);
        if (Math.Abs(det) < 1e-14) return false;
        double inv = 1.0 / det;
        V3 tv = o - a;
        double u = V3.Dot(tv, pv) * inv;
        if (u < -1e-9 || u > 1 + 1e-9) return false;
        V3 qv = V3.Cross(tv, e1);
        double v = V3.Dot(d, qv) * inv;
        if (v < -1e-9 || u + v > 1 + 1e-9) return false;
        t = V3.Dot(e2, qv) * inv;
        return true;
    }

    /// Noktanın üçgene en kısa mesafesinin karesi (Ericson, Real-Time Collision Detection)
    public static double DistSq(V3 p, V3 a, V3 b, V3 c)
    {
        V3 ab = b - a, ac = c - a, ap = p - a, q;
        double d1 = V3.Dot(ab, ap), d2 = V3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) q = a;
        else
        {
            V3 bp = p - b; double d3 = V3.Dot(ab, bp), d4 = V3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) q = b;
            else
            {
                double vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0) q = a + ab * (d1 / (d1 - d3));
                else
                {
                    V3 cp = p - c; double d5 = V3.Dot(ab, cp), d6 = V3.Dot(ac, cp);
                    if (d6 >= 0 && d5 <= d6) q = c;
                    else
                    {
                        double vb = d5 * d2 - d1 * d6;
                        if (vb <= 0 && d2 >= 0 && d6 <= 0) q = a + ac * (d2 / (d2 - d6));
                        else
                        {
                            double va = d3 * d6 - d5 * d4;
                            if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) q = b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
                            else { double den = 1.0 / (va + vb + vc); q = a + ab * (vb * den) + ac * (vc * den); }
                        }
                    }
                }
            }
        }
        V3 r = p - q; return V3.Dot(r, r);
    }

    /// Nokta keep-out gövdesinin içinde mi (ışın parite testi, eğik yön => kenar/köşe isabeti yok)
    static readonly V3 RayDir = new V3(0.5773, 0.5779, 0.5768).Unit();
    public static bool Inside(V3 p, BodyInfo K, TriSoup s)
    {
        if (p.X < K.Min.X || p.Y < K.Min.Y || p.Z < K.Min.Z || p.X > K.Max.X || p.Y > K.Max.Y || p.Z > K.Max.Z) return false;
        int cnt = 0;
        for (int t = K.TriStart; t < K.TriStart + K.TriCount; t++)
        {
            double d; if (RayTri(p, RayDir, s.A(t), s.B(t), s.C(t), out d) && d > 0) cnt++;
        }
        return (cnt & 1) == 1;
    }
}

// ------------------------------------------------------------------------------------
//  Rapor metni (NX ve test ortak)
// ------------------------------------------------------------------------------------
public static class Report
{
    public static string Fmt(double v, int dec) { return v.ToString("F" + dec, CultureInfo.InvariantCulture); }

    public static List<string> Summary(SealantResult r, Settings cfg, double outScale, string unitName)
    {
        var L = new List<string>();
        V3 cg = r.Total.Cg() * (1.0 / outScale);
        double[] I = r.InertiaAtCg();
        L.Add("================ SEALANT HESABI ================");
        L.Add(string.Format("Boşluk aralığı      : {0} - {1} mm, açı tol. {2}°, örnekleme {3} mm",
            Fmt(cfg.MinGap, 2), Fmt(cfg.MaxGap, 2), Fmt(cfg.MaxAngleDeg, 0), Fmt(cfg.SampleSize, 2)));
        L.Add(string.Format("Gövde / aktif       : {0} / {1}   üçgen: {2}  aday üçgen: {3}",
            r.Bodies, r.ActiveBodies, r.Triangles, r.CandidateTriangles));
        L.Add(string.Format("Sealant alanı       : {0} cm2", Fmt(r.Total.Area / 100.0, 2)));
        L.Add(string.Format("Sealant hacmi       : {0} cm3", Fmt(r.Total.Vol / 1000.0, 3)));
        L.Add(string.Format("Yoğunluk            : {0} g/cm3", Fmt(cfg.DensityGcm3, 3)));
        L.Add(string.Format("AĞIRLIK             : {0} kg  ({1} g)", Fmt(r.MassKg, 4), Fmt(r.MassKg * 1000, 1)));
        L.Add(string.Format("CG ({0})            : X={1}  Y={2}  Z={3}", unitName, Fmt(cg.X, 3), Fmt(cg.Y, 3), Fmt(cg.Z, 3)));
        L.Add(string.Format("Atalet @CG (kg·mm2) : Ixx={0} Iyy={1} Izz={2} Ixy={3} Ixz={4} Iyz={5}",
            Fmt(I[0], 2), Fmt(I[1], 2), Fmt(I[2], 2), Fmt(I[3], 2), Fmt(I[4], 2), Fmt(I[5], 2)));
        return L;
    }

    public static List<KeyValuePair<long, Acc>> SortedPairs(SealantResult r)
    {
        var list = new List<KeyValuePair<long, Acc>>(r.Pairs);
        list.Sort((x, y) => y.Value.Vol.CompareTo(x.Value.Vol));
        return list;
    }

    public static string Csv(SealantResult r, List<BodyInfo> bodies, double outScale)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Govde A;Govde B;Alan [cm2];Hacim [cm3];Ortalama bosluk [mm];Agirlik [g];CG X;CG Y;CG Z");
        foreach (var kv in SortedPairs(r))
        {
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffffL); Acc p = kv.Value; V3 c = p.Cg() * (1.0 / outScale);
            sb.AppendLine(string.Join(";", new string[] {
                bodies[a].Name, bodies[b].Name, Fmt(p.Area / 100, 3), Fmt(p.Vol / 1000, 4), Fmt(p.Vol / Math.Max(p.Area, 1e-12), 3),
                Fmt(p.Vol * r.DensityGcm3 * 1e-3, 3), Fmt(c.X, 3), Fmt(c.Y, 3), Fmt(c.Z, 3) }));
        }
        return sb.ToString();
    }

    /// Türkçe karakterleri ASCII'ye çevirip büyük harf yapar (kültürden bağımsız eşleşme)
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case 'ş': case 'Ş': sb.Append('S'); break;
                case 'ç': case 'Ç': sb.Append('C'); break;
                case 'ğ': case 'Ğ': sb.Append('G'); break;
                case 'ı': case 'İ': case 'i': case 'I': sb.Append('I'); break;
                case 'ö': case 'Ö': sb.Append('O'); break;
                case 'ü': case 'Ü': sb.Append('U'); break;
                default: sb.Append(char.ToUpperInvariant(ch)); break;
            }
        }
        return sb.ToString();
    }

    public static bool MatchesAny(string text, string[] keywords)
    {
        string t = Norm(text);
        foreach (string k in keywords) if (k.Length > 0 && t.Contains(Norm(k))) return true;
        return false;
    }
}

#if !SEALANT_TEST
// =====================================================================================
//  NX TARAFI: montajı gez, gövdeleri facet'le (prototip başına bir kez), dünya koordinatına
//  taşı, çözücüyü çalıştır, raporla.
// =====================================================================================
public class SealantCG
{
    static Session theSession;
    static UFSession uf;
    static ListingWindow lw;

    sealed class LocalMesh { public double[] P; public double[] N; public int Count; }
    sealed class Xf { public double[] R = new double[9]; public V3 T; }   // q = R*p + T (mm)

    public static void Main(string[] args)
    {
        theSession = Session.GetSession();
        uf = UFSession.GetUFSession();
        lw = theSession.ListingWindow; lw.Open();
        var cfg = new Settings();
        var swAll = Stopwatch.StartNew();

        Part work = theSession.Parts.Work, disp = theSession.Parts.Display;
        if (work == null || disp == null || work.Tag != disp.Tag)
        { lw.WriteLine("Montajı açıp hem Displayed hem Work part yapın, sonra tekrar çalıştırın."); return; }
        double workToMm = work.PartUnits == BasePart.Units.Inches ? 25.4 : 1.0;

        // ---- Kullanıcı seçimi ile ek hariç tutma ---------------------------------------
        var exclComps = new HashSet<Tag>(); var exclBodies = new HashSet<Tag>();
        if (cfg.AskUserSelection) AskExclusions(exclComps, exclBodies);

        // ---- Gövdeleri topla --------------------------------------------------------------
        var items = new List<BodyItem>();
        foreach (Body b in work.Bodies) Collect(items, b, b, null, work, Role(cfg, b.Name, "", b, null, false), cfg);
        Component root = work.ComponentAssembly.RootComponent;
        if (root != null) Walk(items, root, false, cfg, exclComps);
        foreach (BodyItem it in items) if (exclBodies.Contains(it.Occ.Tag) && it.Role == BodyRole.Normal) it.Role = BodyRole.Excluded;
        lw.WriteLine(string.Format("Toplanan solid gövde: {0}", items.Count));

        // ---- NX bounding box ile ön eleme: hiçbir komşusu olmayanı facet'leme -------------
        if (cfg.PrePruneWithNxBox) PrePrune(items, cfg, workToMm);

        // ---- Facet + dönüşüm ----------------------------------------------------------------
        var sw = Stopwatch.StartNew();
        var soup = new TriSoup(cfg.FacetSize);
        var bodies = new List<BodyInfo>();
        var cache = new Dictionary<Tag, LocalMesh>();
        int done = 0, skipped = 0, fallback = 0;
        foreach (BodyItem it in items)
        {
            if (it.Pruned) continue;
            if ((++done % 50) == 0) uf.Ui.SetStatus(string.Format("Sealant: facet {0}/{1}", done, items.Count));
            try
            {
                LocalMesh mesh; Xf xf;
                if (!TryProtoTransform(it, workToMm, out xf))
                {   // dönüşüm çıkarılamadı: occurrence gövdesini doğrudan facet'le
                    mesh = Facet(it.Occ.Tag, workToMm, cfg); xf = Identity(); fallback++;
                }
                else if (!cache.TryGetValue(it.Proto.Tag, out mesh))
                {
                    mesh = Facet(it.Proto.Tag, it.ProtoToMm, cfg); cache[it.Proto.Tag] = mesh;
                }
                var bi = new BodyInfo(); bi.Name = it.Name; bi.Role = it.Role; bi.TriStart = soup.Count;
                AppendTransformed(soup, mesh, xf, bodies.Count);
                bi.TriCount = soup.Count - bi.TriStart;
                for (int t = bi.TriStart; t < soup.Count; t++)
                {
                    V3 mn, mx; soup.TriBox(t, out mn, out mx);
                    bi.Min = new V3(Math.Min(bi.Min.X, mn.X), Math.Min(bi.Min.Y, mn.Y), Math.Min(bi.Min.Z, mn.Z));
                    bi.Max = new V3(Math.Max(bi.Max.X, mx.X), Math.Max(bi.Max.Y, mx.Y), Math.Max(bi.Max.Z, mx.Z));
                }
                bodies.Add(bi);
            }
            catch (Exception ex) { skipped++; lw.WriteLine("  ! atlandı: " + it.Name + "  (" + ex.Message + ")"); }
        }
        lw.WriteLine(string.Format("Facet: {0} gövde, {1} benzersiz prototip, {2} üçgen, {3} doğrudan facet, {4} atlanan  ({5:F1} s)",
            bodies.Count, cache.Count, soup.Count, fallback, skipped, sw.Elapsed.TotalSeconds));

        // ---- Hesap ---------------------------------------------------------------------------
        uf.Ui.SetStatus("Sealant: hesaplanıyor...");
        sw.Restart();
        SealantResult r = SealantSolver.Run(soup, bodies, cfg);
        lw.WriteLine(string.Format("Hesap süresi: {0:F1} s   (toplam {1:F1} s)", sw.Elapsed.TotalSeconds, swAll.Elapsed.TotalSeconds));

        string unitName = workToMm == 1.0 ? "mm" : "in";
        foreach (string line in Report.Summary(r, cfg, workToMm, unitName)) lw.WriteLine(line);
        lw.WriteLine("---- En çok sealant alan 15 çift ----");
        int shown = 0;
        foreach (var kv in Report.SortedPairs(r))
        {
            if (shown++ >= 15) break;
            int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffffL);
            lw.WriteLine(string.Format("  {0,9} g  {1}  <->  {2}", Report.Fmt(kv.Value.Vol * cfg.DensityGcm3 * 1e-3, 2), bodies[a].Name, bodies[b].Name));
        }

        if (cfg.WriteCsv && r.Pairs.Count > 0)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "sealant_" + SafeName(work.Leaf) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                File.WriteAllText(path, Report.Csv(r, bodies, workToMm), new UTF8Encoding(true));
                lw.WriteLine("CSV raporu: " + path);
            }
            catch (Exception ex) { lw.WriteLine("CSV yazılamadı: " + ex.Message); }
        }
        if (cfg.CreateCgPoint && r.Total.Vol > 0)
        {
            try
            {
                V3 c = r.Total.Cg() * (1.0 / workToMm);
                NXOpen.Point pt = work.Points.CreatePoint(new Point3d(c.X, c.Y, c.Z));
                pt.SetVisibility(SmartObject.VisibilityOption.Visible);
                pt.SetName("SEALANT_CG");
                lw.WriteLine("CG noktası oluşturuldu: SEALANT_CG");
            }
            catch (Exception ex) { lw.WriteLine("CG noktası oluşturulamadı: " + ex.Message); }
        }
        uf.Ui.SetStatus("Sealant: tamamlandı");
    }

    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Immediately; }

    // ------------------------------------------------------------------------------------
    sealed class BodyItem
    {
        public Body Occ, Proto; public Component Comp; public string Name; public BodyRole Role;
        public double ProtoToMm = 1.0; public bool Pruned;
    }

    static void Walk(List<BodyItem> items, Component c, bool parentExcluded, Settings cfg, HashSet<Tag> exclComps)
    {
        if (c.IsSuppressed) return;
        Part proto = c.Prototype as Part;
        string partName = proto != null ? proto.Leaf : "";
        bool keep = Report.MatchesAny(c.DisplayName + " " + c.Name, new string[] { cfg.KeepOutName });
        bool excl = parentExcluded || exclComps.Contains(c.Tag)
                    || Report.MatchesAny(c.DisplayName + " " + c.Name + " " + partName, cfg.ExcludeKeywords)
                    || HasAttr(c, cfg.ExcludeAttribute) || (proto != null && HasAttr(proto, cfg.ExcludeAttribute));
        if (proto != null)
        {
            if (!proto.IsFullyLoaded) { try { proto.LoadThisPartFully(); } catch { } }
            foreach (Body b in proto.Bodies)
            {
                if (!b.IsSolidBody) continue;
                Body occ = c.FindOccurrence(b) as Body;      // referans set'te değilse null
                if (occ == null) continue;
                BodyRole r = keep ? BodyRole.KeepOut : Role(cfg, b.Name, c.DisplayName, b, c, excl);
                Collect(items, occ, b, c, proto, r, cfg);
            }
        }
        foreach (Component ch in c.GetChildren()) Walk(items, ch, excl, cfg, exclComps);
    }

    static BodyRole Role(Settings cfg, string bodyName, string compName, Body b, Component c, bool compExcluded)
    {
        if (Report.MatchesAny(bodyName, new string[] { cfg.KeepOutName })) return BodyRole.KeepOut;
        if (cfg.KeepOutLayer > 0 && c == null && b.Layer == cfg.KeepOutLayer) return BodyRole.KeepOut;
        if (compExcluded || Report.MatchesAny(bodyName, cfg.ExcludeKeywords) || HasAttr(b, cfg.ExcludeAttribute)) return BodyRole.Excluded;
        return BodyRole.Normal;
    }

    static void Collect(List<BodyItem> items, Body occ, Body proto, Component c, Part protoPart, BodyRole role, Settings cfg)
    {
        var it = new BodyItem();
        it.Occ = occ; it.Proto = proto; it.Comp = c; it.Role = role;
        it.ProtoToMm = protoPart.PartUnits == BasePart.Units.Inches ? 25.4 : 1.0;
        string bn = string.IsNullOrEmpty(proto.Name) ? proto.JournalIdentifier : proto.Name;
        it.Name = (c != null ? CompPath(c) : "<root>") + "/" + bn;
        items.Add(it);
    }

    static string CompPath(Component c)
    {
        var parts = new List<string>();
        for (Component x = c; x != null && x.Parent != null; x = x.Parent) parts.Insert(0, x.DisplayName);
        return string.Join(">", parts.ToArray());
    }

    static bool HasAttr(NXObject o, string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        try { return o.HasUserAttribute(title, NXObject.AttributeType.Any, -1); } catch { return false; }
    }

    static void AskExclusions(HashSet<Tag> comps, HashSet<Tag> bodies)
    {
        try
        {
            TaggedObject[] sel;
            var mask = new Selection.MaskTriple[] {
                new Selection.MaskTriple(UFConstants.UF_component_type, 0, 0),
                new Selection.MaskTriple(UFConstants.UF_solid_type, 0, UFConstants.UF_UI_SEL_FEATURE_BODY) };
            Selection.Response resp = UI.GetUI().SelectionManager.SelectTaggedObjects(
                "Sealant atılmayacak bağlantı parçalarını seçin (menteşe vb.). Yoksa OK.",
                "Sealant - hariç tutulacaklar", Selection.SelectionScope.AnyInAssembly,
                Selection.SelectionAction.ClearAndEnableSpecific, false, false, mask, out sel);
            if (resp != Selection.Response.Ok || sel == null) return;
            foreach (TaggedObject o in sel)
            {
                if (o is Component) comps.Add(o.Tag);
                else if (o is Body) bodies.Add(o.Tag);
            }
            lw.WriteLine(string.Format("Kullanıcı seçimi: {0} bileşen, {1} gövde hariç tutuldu", comps.Count, bodies.Count));
        }
        catch (Exception ex) { lw.WriteLine("Seçim atlandı: " + ex.Message); }
    }

    /// NX'in (gevşek) bounding box'ı ile: MaxGap içinde normal komşusu olmayan gövdeleri ele.
    static void PrePrune(List<BodyItem> items, Settings cfg, double workToMm)
    {
        int n = items.Count; var mn = new V3[n]; var mx = new V3[n]; var ok = new bool[n];
        double[] box = new double[6];
        for (int i = 0; i < n; i++)
        {
            try
            {
                uf.Modl.AskBoundingBox(items[i].Occ.Tag, box);
                mn[i] = new V3(box[0], box[1], box[2]) * workToMm; mx[i] = new V3(box[3], box[4], box[5]) * workToMm; ok[i] = true;
            }
            catch { ok[i] = false; }
        }
        int[] order = new int[n]; double[] key = new double[n];
        for (int i = 0; i < n; i++) { order[i] = i; key[i] = ok[i] ? mn[i].X : double.MinValue; }
        Array.Sort(key, order);
        var need = new bool[n];
        double pad = cfg.MaxGap + 1.0;
        for (int ii = 0; ii < n; ii++)
        {
            int a = order[ii]; if (!ok[a]) { need[a] = true; continue; }
            for (int jj = ii + 1; jj < n; jj++)
            {
                int b = order[jj]; if (!ok[b]) continue;
                if (mn[b].X > mx[a].X + pad) break;
                BodyRole ra = items[a].Role, rb = items[b].Role;
                if (ra == BodyRole.KeepOut || rb == BodyRole.KeepOut) continue;
                if (ra != BodyRole.Normal && rb != BodyRole.Normal) continue;
                if (!SealantSolver.BoxOverlap(mn[a], mx[a], mn[b], mx[b], pad)) continue;
                need[a] = true; need[b] = true;
            }
        }
        int pruned = 0;
        for (int i = 0; i < n; i++)
        {
            if (items[i].Role == BodyRole.KeepOut) continue;   // keep-out'lar az ve gerekli
            if (!need[i]) { items[i].Pruned = true; pruned++; }
        }
        lw.WriteLine(string.Format("Ön eleme: {0} gövdenin MaxGap içinde komşusu yok, facet'lenmeyecek", pruned));
    }

    /// Prototip -> montaj dönüşümünü, prototip kenar köşeleri ile occurrence köşelerinin eşleşmesinden
    /// tam olarak çıkarır (bileşen konum API'lerinin göreli/mutlak belirsizliğine bağımlı değil).
    static bool TryProtoTransform(BodyItem it, double workToMm, out Xf xf)
    {
        xf = null;
        if (it.Comp == null) { xf = Identity(); return it.ProtoToMm == workToMm; }
        var P = new List<V3>(); var Q = new List<V3>();
        Edge[] edges = it.Proto.GetEdges();
        int step = Math.Max(1, edges.Length / 16);
        for (int i = 0; i < edges.Length && P.Count < 48; i += step)
        {
            Edge oe = it.Comp.FindOccurrence(edges[i]) as Edge;
            if (oe == null) continue;
            Point3d p1, p2, q1, q2;
            edges[i].GetVertices(out p1, out p2); oe.GetVertices(out q1, out q2);
            P.Add(new V3(p1.X, p1.Y, p1.Z) * it.ProtoToMm); Q.Add(new V3(q1.X, q1.Y, q1.Z) * workToMm);
            P.Add(new V3(p2.X, p2.Y, p2.Z) * it.ProtoToMm); Q.Add(new V3(q2.X, q2.Y, q2.Z) * workToMm);
        }
        if (P.Count < 3) return false;
        int i1 = 0; double best = 0;
        for (int i = 1; i < P.Count; i++) { double d = (P[i] - P[0]).Len(); if (d > best) { best = d; i1 = i; } }
        if (best < 1e-6) return false;
        int i2 = -1; double bestA = 0;
        for (int i = 1; i < P.Count; i++) { double a = V3.Cross(P[i1] - P[0], P[i] - P[0]).Len(); if (a > bestA) { bestA = a; i2 = i; } }
        if (i2 < 0 || bestA < 1e-6 * best * best) return false;   // köşeler doğrusal
        V3[] fp = Frame(P[0], P[i1], P[i2]), fq = Frame(Q[0], Q[i1], Q[i2]);
        xf = new Xf();
        for (int r = 0; r < 3; r++)       // R = Fq * Fp^T  (sütunlar = çerçeve eksenleri)
            for (int c = 0; c < 3; c++)
                xf.R[3 * r + c] = Comp(fq[0], r) * Comp(fp[0], c) + Comp(fq[1], r) * Comp(fp[1], c) + Comp(fq[2], r) * Comp(fp[2], c);
        xf.T = Q[0] - Apply(xf, P[0], false);
        double tol = 1e-3 * Math.Max(1.0, best);
        for (int i = 0; i < P.Count; i++) if ((Apply(xf, P[i], true) - Q[i]).Len() > tol) return false;  // doğrula
        return true;
    }

    static V3[] Frame(V3 a, V3 b, V3 c)
    {
        V3 e1 = (b - a).Unit(); V3 e3 = V3.Cross(e1, c - a).Unit(); V3 e2 = V3.Cross(e3, e1);
        return new V3[] { e1, e2, e3 };
    }
    static double Comp(V3 v, int i) { return i == 0 ? v.X : (i == 1 ? v.Y : v.Z); }
    static Xf Identity() { var x = new Xf(); x.R[0] = x.R[4] = x.R[8] = 1; x.T = new V3(0, 0, 0); return x; }
    static V3 Apply(Xf x, V3 p, bool translate)
    {
        double[] R = x.R;
        var q = new V3(R[0] * p.X + R[1] * p.Y + R[2] * p.Z, R[3] * p.X + R[4] * p.Y + R[5] * p.Z, R[6] * p.X + R[7] * p.Y + R[8] * p.Z);
        return translate ? q + x.T : q;
    }

    static void AppendTransformed(TriSoup soup, LocalMesh m, Xf xf, int body)
    {
        for (int t = 0; t < m.Count; t++)
        {
            int i = 9 * t, j = 3 * t;
            soup.Add(Apply(xf, new V3(m.P[i], m.P[i + 1], m.P[i + 2]), true),
                     Apply(xf, new V3(m.P[i + 3], m.P[i + 4], m.P[i + 5]), true),
                     Apply(xf, new V3(m.P[i + 6], m.P[i + 7], m.P[i + 8]), true),
                     Apply(xf, new V3(m.N[j], m.N[j + 1], m.N[j + 2]), false), body);
        }
    }

    /// UF_FACET ile gövdeyi üçgenlere böler; koordinatlar mm'ye çevrilir. Facet modeli silinir.
    static LocalMesh Facet(Tag body, double toMm, Settings cfg)
    {
        UFFacet.Parameters fp;
        uf.Facet.AskDefaultParameters(out fp);
        fp.max_facet_edges = 3;
        fp.specify_surface_tolerance = true;
        fp.surface_dist_tolerance = cfg.FacetChordTol / toMm;
        fp.surface_angular_tolerance = 0.2;          // rad
        fp.specify_max_facet_size = true;
        fp.max_facet_size = cfg.FacetSize / toMm;
        fp.specify_convex_facets = false;
        Tag model;
        uf.Facet.FacetSolid(body, ref fp, out model);
        var m = new LocalMesh();
        try
        {
            int nf; uf.Facet.AskNFacetsInModel(model, out nf);
            int maxV; uf.Facet.AskMaxFacetVerts(model, out maxV);
            m.P = new double[9 * Math.Max(1, nf) * Math.Max(1, maxV - 2)];
            m.N = new double[3 * Math.Max(1, nf) * Math.Max(1, maxV - 2)];
            double[,] vv = new double[maxV, 3], nn = new double[maxV, 3];
            int fid = UFConstants.UF_FACET_NULL_FACET_ID;
            uf.Facet.CycleFacets(model, ref fid);
            while (fid != UFConstants.UF_FACET_NULL_FACET_ID)
            {
                int nv; uf.Facet.AskVerticesOfFacet(model, fid, out nv, vv);
                V3 avgN = new V3(0, 0, 0);
                try { int nn2; uf.Facet.AskNormalsOfFacet(model, fid, out nn2, nn); for (int k = 0; k < nn2; k++) avgN = avgN + new V3(nn[k, 0], nn[k, 1], nn[k, 2]); }
                catch { }
                V3 a = new V3(vv[0, 0], vv[0, 1], vv[0, 2]) * toMm;
                for (int k = 1; k + 1 < nv; k++)      // çokgen ise fan ile üçgenle
                {
                    V3 b = new V3(vv[k, 0], vv[k, 1], vv[k, 2]) * toMm, c = new V3(vv[k + 1, 0], vv[k + 1, 1], vv[k + 1, 2]) * toMm;
                    V3 n = V3.Cross(b - a, c - a);
                    if (n.Len() < 1e-12) continue;
                    n = n.Unit();
                    if (avgN.Len() > 0 && V3.Dot(n, avgN) < 0) { n = n * -1; V3 tmp = b; b = c; c = tmp; }
                    if (m.Count * 9 >= m.P.Length) { Array.Resize(ref m.P, m.P.Length * 2); Array.Resize(ref m.N, m.N.Length * 2); }
                    int i = 9 * m.Count;
                    m.P[i] = a.X; m.P[i + 1] = a.Y; m.P[i + 2] = a.Z; m.P[i + 3] = b.X; m.P[i + 4] = b.Y; m.P[i + 5] = b.Z;
                    m.P[i + 6] = c.X; m.P[i + 7] = c.Y; m.P[i + 8] = c.Z;
                    m.N[3 * m.Count] = n.X; m.N[3 * m.Count + 1] = n.Y; m.N[3 * m.Count + 2] = n.Z;
                    m.Count++;
                }
                uf.Facet.CycleFacets(model, ref fid);
            }
        }
        finally { try { uf.Obj.DeleteObject(model); } catch { } }
        return m;
    }

    static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
#endif
