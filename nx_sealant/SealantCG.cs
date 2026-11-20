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
    // =================================================================================
    //  HIZLI ÇALIŞMA İÇİN ÖNERİLER  (satır sonlarındaki "HIZLI:" notlarına bakın)
    //    SampleSize    1.0  -> 2.0    hesap ~4 kat hızlanır, hacim çok az değişir   (en etkili)
    //    FacetChordTol 0.02 -> 0.05   eğrisel yüzeylerde daha az üçgen, facet hızlanır
    //    FacetSize     8    -> 15     büyük düz yüzeylerde daha az üçgen
    //    ExcludeMargin 10   -> 3      çok bağlantı elemanı varsa hesap hızlanır
    //    MaxGap        gereğinden büyük tutmayın
    //    ShowInNx      sadece ağırlık lazımsa false (NX'te nesne oluşturma atlanır)
    //    MaxDisplayTriangles 3000000 -> 1000000   NX'in ağırlaşmasını önler
    //  Değiştirmeyin: MaxThreads = 0, PrePruneWithNxBox = true
    //  Ayrıca montajı partial load yerine tam yüklenmiş açmak facet adımını hızlandırır.
    // =================================================================================

    // --- Boşluk tanımı -------------------------------------------------------------
    public double MinGap = 0.10;        // mm  bundan küçük boşluk = temas (faying, aşağıya bakın)
    public double MaxGap = 3.00;        // mm  bundan büyük açıklık sealant ile doldurulmaz
    public double MaxAngleDeg = 25.0;   // iki yüzey birbirine bu açıdan daha paralelse "karşılıklı"

    // --- Temas eden yüzeyler (faying surface sealant) -----------------------------------
    // Birbirine değen (boşluğu MinGap'ten küçük) karşılıklı yüzeylere FayingThickness
    // kalınlığında sealant tabakası sayılır. Bağlantı elemanı / emniyet payı / keep-out
    // kuralları burada da geçerlidir. Raporda boşluk sealant'ından ayrı yazılır.
    public bool IncludeFaying = true;
    public double FayingThickness = 0.10; // mm  -> proses spesifikasyonunuzdaki film kalınlığını girin

    // --- Çözünürlük / hız ------------------------------------------------------------
    public double SampleSize = 1.0;     // mm  örnekleme aralığı (küçük = hassas, yavaş)      HIZLI: 2.0
    public double FacetSize = 8.0;      // mm  facet üçgenlerinin en uzun kenarı                HIZLI: 15
    public double FacetChordTol = 0.02; // mm  facet kiriş toleransı (eğrisel yüzey sadakati)   HIZLI: 0.05
    public int MaxThreads = 0;          // 0 = tüm çekirdekler (değiştirmeyin)

    // --- Malzeme / ağırlık katsayısı -------------------------------------------------------
    // Ağırlık = hacim [cm3] x DensityGcm3  -> gram.  Kullandığınız sealant'ın yoğunluğunu girin.
    // Excel'de de B2 hücresine yazılır; orada değiştirirseniz ağırlık sütunu yeniden hesaplanır.
    public double DensityGcm3 = 1.55;   // g/cm3  <<< KATSAYI

    // --- Hariç tutma (bağlantı bölgeleri) -------------------------------------------
    // Bileşen adı / part adı / gövde adı bu kelimelerden birini içerirse (Türkçe karakterler
    // ASCII'ye çevrilip büyük harfle aranır) o parça "bağlantı elemanı" sayılır: ona olan
    // boşluk hesaplanmaz ve çevresindeki ExcludeMargin mesafesi de sealant'sız kalır.
    public string[] ExcludeKeywords = new string[] {
        "HINGE", "MENTESE", "BOLT", "CIVATA", "SCREW", "VIDA", "NUT", "SOMUN",
        "WASHER", "PUL", "PIN", "BUSH", "BURC", "BEARING", "RULMAN", "RIVET", "PERCIN",
        "FASTENER", "LUG" };
    // Adı bu metinlerden biriyle BAŞLAYAN bileşen/part/gövde hariç tutulur (ör. parça numarası ön eki)
    public string[] ExcludePrefixes = new string[] { };
    public string ExcludeAttribute = "SEALANT_EXCLUDE"; // bu attribute'u olan bileşen/part hariç
    public double ExcludeMargin = 10.0;  // mm  hariç parçaların çevresindeki sealant'sız bölge  HIZLI: 3
    public bool AskUserSelection = true; // çalışırken ek hariç parça/gövde seçtir

    // Keep-out gövdeleri: kullanıcı tarafından modellenen basit "yasak hacim" solidleri.
    // İçine düşen sealant sayılmaz (ör. menteşe ekseni etrafına bir silindir çizin).
    public string KeepOutName = "SEAL_KEEPOUT"; // gövde/bileşen adı bunu içeriyorsa
    public int KeepOutLayer = 250;              // ya da bu layer'daysa  (0 = kapalı)

    // --- Çıktılar --------------------------------------------------------------------
    public bool CreateCgPoint = true;    // CG'ye "SEALANT_CG" isimli nokta koy
    public bool WriteExcel = true;       // parça çifti bazında Excel (.xlsx) raporu
    public string OutputFolder = "";     // Excel/STL klasörü; boş = Masaüstü
    // Excel'deki "Parça No": önce part'ın bu attribute'u okunur (Teamcenter'da item ID),
    // yoksa part adının "/" öncesi (ItemID/Rev -> ItemID) kullanılır.
    public string PartNoAttribute = "DB_PART_NO";
    public bool PrePruneWithNxBox = true;// faceting öncesi NX bounding box ile eleme

    // --- NX'te gösterim ------------------------------------------------------------------
    // Sealant atılan bölgeler, boşluğun orta düzleminde FOSFOR YEŞİLİ facet nesneleri olarak
    // montaj (work) part'ına eklenir: her parça çifti ayrı nesne, adı SEALANT_001, SEALANT_002 ...
    // (Excel'deki "NX Nesnesi" sütunu ile aynı). Component part'lar DEĞİŞTİRİLMEZ.
    // Makro tekrar çalışınca eski SEALANT_* nesneleri silinir.
    public bool ShowInNx = true;              // HIZLI: sadece ağırlık lazımsa false
    // NX standart paletinde renk no = 1 + 36*(5-G) + 6*(5-R) + (5-B)   (R,G,B: 0..5 kademe)
    // 30 = fosfor yeşili (R51 G255 B0), 36 = saf yeşil. Paletiniz farklıysa numarayı değiştirin.
    public int SealantColor = 30;
    public int DisplayLayer = 251;            // sealant nesnelerinin layer'ı (aç/kapa için)
    public int MaxDisplayTriangles = 3000000; // bundan fazlaysa NX'te çizilmez, sadece STL yazılır  HIZLI: 1000000
    public bool WriteStl = true;              // sealant bölgelerini STL olarak da kaydet (OutputFolder)
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
    public string PartNo = "";       // Excel için parça numarası
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
    public double FayArea;   // bu alanın temas eden (faying) kısmı
    public void AddPoint(double dA, double dV, V3 c)
    {
        Area += dA; Vol += dV; Samples++;
        Sx += dV * c.X; Sy += dV * c.Y; Sz += dV * c.Z;
        Sxx += dV * c.X * c.X; Syy += dV * c.Y * c.Y; Szz += dV * c.Z * c.Z;
        Sxy += dV * c.X * c.Y; Sxz += dV * c.X * c.Z; Syz += dV * c.Y * c.Z;
    }
    public void Merge(Acc o)
    {
        Area += o.Area; Vol += o.Vol; Samples += o.Samples; FayArea += o.FayArea; Sx += o.Sx; Sy += o.Sy; Sz += o.Sz;
        Sxx += o.Sxx; Syy += o.Syy; Szz += o.Szz; Sxy += o.Sxy; Sxz += o.Sxz; Syz += o.Syz;
    }
    public V3 Cg() { return Vol > 0 ? new V3(Sx / Vol, Sy / Vol, Sz / Vol) : new V3(0, 0, 0); }
}

public sealed class SealantResult
{
    public Acc Total = new Acc();
    public Acc Faying = new Acc();     // Total'ın temas eden yüzeylerden gelen kısmı
    public Dictionary<long, Acc> Pairs = new Dictionary<long, Acc>();   // (A<<32)|B
    public double DensityGcm3;
    public int Bodies, ActiveBodies, Triangles, CandidateTriangles;
    /// Gösterim üçgenleri (mm, boşluğun orta düzleminde), parça çifti anahtarı ile: her üçgen 9 float
    public Dictionary<long, List<float>> Display = new Dictionary<long, List<float>>();
    public long DisplayTriangleCount()
    {
        long n = 0; foreach (var kv in Display) n += kv.Value.Count / 9; return n;
    }
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
        int nThreads = Math.Max(1, cfg.MaxThreads > 0 ? cfg.MaxThreads : Environment.ProcessorCount);
        BodyRole[] role = new BodyRole[nb];
        for (int i = 0; i < nb; i++) role[i] = bodies[i].Role;

        // Her iş parçacığı kendi Worker'ına yazar (kilit yok); k. iş parçacığı k, k+T, k+2T ... üçgenlerini işler.
        // Sadece Task kullanılır: NX journal derleyicisinin her sürümünde mevcut (Parallel sınıfı olmayabilir).
        var workers = new Worker[nThreads];
        var tasks = new Task[nThreads];
        for (int k = 0; k < nThreads; k++)
        {
            int kk = k;
            workers[kk] = new Worker();
            tasks[kk] = Task.Factory.StartNew(delegate
            {
                Worker w = workers[kk];
                for (int ci = kk; ci < candTris.Count; ci += nThreads)
                    ProcessTriangle(candTris[ci], s, role, grid, exGrid, keepOuts, cfg, cosTol, margin, w);
            }, TaskCreationOptions.LongRunning);
        }
        Task.WaitAll(tasks);

        foreach (Worker w in workers)
        {
            res.Total.Merge(w.Total);
            res.Faying.Merge(w.Faying);
            foreach (var kv in w.Pairs)
            {
                Acc a; if (!res.Pairs.TryGetValue(kv.Key, out a)) { a = new Acc(); res.Pairs[kv.Key] = a; }
                a.Merge(kv.Value);
            }
            foreach (var kv in w.Disp)
            {
                List<float> d; if (!res.Display.TryGetValue(kv.Key, out d)) { d = new List<float>(); res.Display[kv.Key] = d; }
                d.AddRange(kv.Value);
            }
        }
        return res;
    }

    sealed class Worker
    {
        public Acc Total = new Acc();
        public Acc Faying = new Acc();
        public Dictionary<long, Acc> Pairs = new Dictionary<long, Acc>();
        public HashSet<int> Set = new HashSet<int>();
        public List<int> Hits = new List<int>();
        public List<int> Excl = new List<int>();
        public List<BodyInfo> Ko = new List<BodyInfo>();
        public Dictionary<long, List<float>> Disp = new Dictionary<long, List<float>>();
        public List<Hit> Pend = new List<Hit>();
    }

    struct Hit { public int I, J; public bool Down; public double Gap; public int B; }

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
        bool show = cfg.ShowInNx || cfg.WriteStl;
        int ok = 0, firstB = -1; bool sameB = true; double sumGap = 0;
        w.Pend.Clear();
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m - i; j++)
            {
                for (int down = 0; down < 2; down++)
                {
                    if (down == 1 && i + j >= m - 1) break;
                    double f = down == 0 ? 1.0 / 3 : 2.0 / 3;     // "yukarı" / "aşağı" alt üçgen merkezi
                    double gap; int hb;
                    if (!Sample(a + e1 * (i + f) + e2 * (j + f), n, dA, A, s, role, cfg, margin, w, out gap, out hb)) continue;
                    ok++; sumGap += gap;
                    if (firstB < 0) firstB = hb; else if (hb != firstB) sameB = false;
                    if (show) { Hit h; h.I = i; h.J = j; h.Down = down == 1; h.Gap = gap; h.B = hb; w.Pend.Add(h); }
                }
            }
        if (!show || ok == 0) return;

        // Gösterim: üçgenin tamamı aynı çifte sealant alıyorsa tek üçgen (ortalama boşluk), değilse
        // sadece sealant alan alt üçgenler. Hepsi boşluğun orta düzlemine ötelenir.
        if (ok == m * m && sameB)
        {
            V3 off = n * (0.5 * sumGap / ok);
            EmitTri(w, Key(A, firstB), a + off, b + off, c + off);
            return;
        }
        foreach (Hit h in w.Pend)
        {
            V3 off = n * (0.5 * h.Gap), p0 = a + e1 * h.I + e2 * h.J + off;
            if (!h.Down) EmitTri(w, Key(A, h.B), p0, p0 + e1, p0 + e2);
            else EmitTri(w, Key(A, h.B), p0 + e1, p0 + e1 + e2, p0 + e2);
        }
    }

    static long Key(int a, int b) { return ((long)a << 32) | (uint)b; }

    static void EmitTri(Worker w, long key, V3 a, V3 b, V3 c)
    {
        List<float> d; if (!w.Disp.TryGetValue(key, out d)) { d = new List<float>(); w.Disp[key] = d; }
        d.Add((float)a.X); d.Add((float)a.Y); d.Add((float)a.Z); d.Add((float)b.X); d.Add((float)b.Y); d.Add((float)b.Z);
        d.Add((float)c.X); d.Add((float)c.Y); d.Add((float)c.Z);
    }

    static bool Sample(V3 p, V3 n, double dA, int A, TriSoup s, BodyRole[] role, Settings cfg, double margin, Worker w,
                       out double gap, out int bestBody)
    {
        V3 o = p - n * Eps;
        gap = 0;
        double best = double.MaxValue; bestBody = -1;
        foreach (int k in w.Hits)
        {
            double t;
            if (RayTri(o, n, s.A(k), s.B(k), s.C(k), out t) && t >= 0 && t < best) { best = t; bestBody = s.Body[k]; }
        }
        if (bestBody < 0) return false;
        double realGap = best - Eps;
        if (realGap > cfg.MaxGap) return false;        // açıklık çok büyük
        bool faying = realGap < cfg.MinGap;            // temas eden yüzeyler
        if (faying && !(cfg.IncludeFaying && cfg.FayingThickness > 0)) return false;
        if (bestBody < A) return false;                // çift sayımı önle: hacmi küçük indeksli gövde sayar
        if (role[bestBody] != BodyRole.Normal) return false;   // menteşe/cıvata ile arasındaki boşluk
        gap = faying ? cfg.FayingThickness : realGap;  // sayılan sealant kalınlığı

        V3 mid = p + n * (0.5 * Math.Max(0, realGap));
        if (w.Excl.Count > 0)
        {
            double m2 = margin * margin;
            foreach (int k in w.Excl) if (DistSq(mid, s.A(k), s.B(k), s.C(k)) < m2) return false;
        }
        foreach (BodyInfo K in w.Ko) if (Inside(mid, K, s)) return false;

        double dV = dA * gap;
        w.Total.AddPoint(dA, dV, mid);
        if (faying) w.Faying.AddPoint(dA, dV, mid);
        long key = Key(A, bestBody);
        Acc pa; if (!w.Pairs.TryGetValue(key, out pa)) { pa = new Acc(); w.Pairs[key] = pa; }
        pa.AddPoint(dA, dV, mid);
        if (faying) { pa.FayArea += dA; w.Faying.FayArea += dA; }
        return true;
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
        if (cfg.IncludeFaying)
        {
            double fv = r.Faying.Vol, gv = r.Total.Vol - fv, rho = cfg.DensityGcm3 * 1e-3;
            L.Add(string.Format("  - boşluk dolgusu  : {0} cm3  {1} g", Fmt(gv / 1000.0, 3), Fmt(gv * rho, 1)));
            L.Add(string.Format("  - temas (faying)  : {0} cm3  {1} g   alan {2} cm2, kalınlık {3} mm", Fmt(fv / 1000.0, 3),
                Fmt(fv * rho, 1), Fmt(r.Faying.Area / 100.0, 2), Fmt(cfg.FayingThickness, 3)));
        }
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

    /// Parça çifti bazında Excel raporu. Ağırlık sütunu formüldür: Hacim x B2 (yoğunluk).
    public static void WriteExcel(string path, SealantResult r, List<BodyInfo> bodies, Settings cfg,
                                  double outScale, string unitName, string title, bool nxNames)
    {
        var pairs = SortedPairs(r);
        const int H = 6;                         // başlık satırı
        int first = H + 1, last = Math.Max(first, H + pairs.Count);
        string u = "[" + unitName + "]";
        var x = new XlsxSheet();
        x.Text(1, 1, "SEALANT RAPORU", 4); x.Text(1, 2, title, 4);
        x.Text(2, 1, "Yoğunluk katsayısı [g/cm³]", 4); x.Num(2, 2, cfg.DensityGcm3, 3);
        x.Text(2, 3, "<- değiştirirseniz ağırlıklar yeniden hesaplanır", 0);
        x.Text(3, 1, "Toplam ağırlık [g]", 4); x.Formula(3, 2, string.Format("SUM(G{0}:G{1})", first, last), 5);
        x.Text(4, 1, "Toplam alan [cm²]", 4); x.Formula(4, 2, string.Format("SUM(C{0}:C{1})", first, last), 5);
        x.Text(5, 1, "Toplam CG X / Y / Z " + u, 4);
        for (int k = 0; k < 3; k++)
        {
            string col = new string[] { "D", "E", "F" }[k];
            x.Formula(5, 2 + k, string.Format("IF($B$3=0,0,SUMPRODUCT({0}{1}:{0}{2},$G${1}:$G${2})/$B$3)", col, first, last), 3);
        }
        string[] head = { "Parça No 1", "Parça No 2", "Yüzey Alanı [cm²]", "CGx " + u, "CGy " + u, "CGz " + u,
                          "Sealant Ağırlık [g]", "Hacim [cm³]", "Ort. Kalınlık [mm]", "Temas (faying) Alanı [cm²]",
                          "NX Nesnesi", "Bileşen 1 (montaj yolu)", "Bileşen 2 (montaj yolu)" };
        for (int k = 0; k < head.Length; k++) x.Text(H, k + 1, head[k], 1);
        int row = H;
        for (int i = 0; i < pairs.Count; i++)
        {
            row++;
            int a = (int)(pairs[i].Key >> 32), b = (int)(pairs[i].Key & 0xffffffffL);
            Acc p = pairs[i].Value; V3 c = p.Cg() * (1.0 / outScale);
            x.Text(row, 1, bodies[a].PartNo, 0); x.Text(row, 2, bodies[b].PartNo, 0);
            x.Num(row, 3, p.Area / 100.0, 2);
            x.Num(row, 4, c.X, 3); x.Num(row, 5, c.Y, 3); x.Num(row, 6, c.Z, 3);
            x.Formula(row, 7, string.Format("H{0}*$B$2", row), 2);
            x.Num(row, 8, p.Vol / 1000.0, 3);
            x.Num(row, 9, p.Vol / Math.Max(p.Area, 1e-12), 3);
            x.Num(row, 10, p.FayArea / 100.0, 2);
            x.Text(row, 11, nxNames ? PairObjectName(i) : "", 0);
            x.Text(row, 12, bodies[a].Name, 0); x.Text(row, 13, bodies[b].Name, 0);
        }
        x.Widths = new double[] { 20, 20, 16, 13, 13, 13, 18, 13, 16, 22, 14, 60, 60 };
        x.FreezeRows = H; x.FilterRef = string.Format("A{0}:M{1}", H, Math.Max(H + 1, row));
        x.Save(path, "Sealant");
    }

    /// Montajdaki gösterim nesnesinin adı (SortedPairs sırasıyla)
    public static string PairObjectName(int index0) { return "SEALANT_" + (index0 + 1).ToString("000"); }

    /// Gösterim üçgenlerini binary STL olarak yazar (birim: mm)
    public static void WriteStl(string path, SealantResult r)
    {
        long n = r.DisplayTriangleCount();
        using (var bw = new BinaryWriter(File.Create(path)))
        {
            byte[] header = new byte[80];
            byte[] txt = Encoding.ASCII.GetBytes("SealantCG sealant regions, units mm");
            Array.Copy(txt, header, txt.Length);
            bw.Write(header); bw.Write((uint)n);
            foreach (var kv in r.Display)
            {
                List<float> d = kv.Value;
                for (int i = 0; i + 8 < d.Count; i += 9)
                {
                    V3 a = new V3(d[i], d[i + 1], d[i + 2]), b = new V3(d[i + 3], d[i + 4], d[i + 5]), c = new V3(d[i + 6], d[i + 7], d[i + 8]);
                    V3 nn = V3.Cross(b - a, c - a).Unit();
                    bw.Write((float)nn.X); bw.Write((float)nn.Y); bw.Write((float)nn.Z);
                    for (int k = 0; k < 9; k++) bw.Write(d[i + k]);
                    bw.Write((ushort)0);
                }
            }
        }
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

    public static bool StartsWithAny(string text, string[] prefixes)
    {
        string t = Norm(text);
        foreach (string k in prefixes) if (k.Length > 0 && t.StartsWith(Norm(k), StringComparison.Ordinal)) return true;
        return false;
    }
}

// ------------------------------------------------------------------------------------
//  Minimal .xlsx yazıcı: Excel'e veya ek kütüphaneye (System.IO.Compression dahil) gerek yok.
//  Zip "store" (sıkıştırmasız) + elle CRC32. Formüller dosya açılınca hesaplanır.
// ------------------------------------------------------------------------------------
public sealed class XlsxSheet
{
    // stil: 0 normal, 1 başlık (kalın + yeşil zemin), 2 "0.00", 3 "0.000", 4 kalın, 5 kalın "0.00"
    readonly SortedDictionary<int, SortedDictionary<int, string>> rows = new SortedDictionary<int, SortedDictionary<int, string>>();
    public double[] Widths; public int FreezeRows; public string FilterRef;

    static string Col(int c) { string s = ""; while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; } return s; }
    static string Esc(string t)
    {
        if (t == null) return "";
        var sb = new StringBuilder(t.Length);
        foreach (char ch in t)
        {
            if (ch == '&') sb.Append("&amp;"); else if (ch == '<') sb.Append("&lt;"); else if (ch == '>') sb.Append("&gt;");
            else if (ch == '"') sb.Append("&quot;"); else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }
    void Put(int r, int c, string xml)
    {
        SortedDictionary<int, string> row;
        if (!rows.TryGetValue(r, out row)) { row = new SortedDictionary<int, string>(); rows[r] = row; }
        row[c] = xml;
    }
    public void Text(int r, int c, string t, int style)
    {
        Put(r, c, string.Format("<c r=\"{0}{1}\" t=\"inlineStr\" s=\"{2}\"><is><t xml:space=\"preserve\">{3}</t></is></c>", Col(c), r, style, Esc(t)));
    }
    public void Num(int r, int c, double v, int style)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
        Put(r, c, string.Format("<c r=\"{0}{1}\" s=\"{2}\"><v>{3}</v></c>", Col(c), r, style, v.ToString("R", CultureInfo.InvariantCulture)));
    }
    public void Formula(int r, int c, string f, int style)
    {
        Put(r, c, string.Format("<c r=\"{0}{1}\" s=\"{2}\"><f>{3}</f></c>", Col(c), r, style, Esc(f)));
    }

    public void Save(string path, string sheetName)
    {
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sheet.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        if (FreezeRows > 0) sheet.AppendFormat("<pane ySplit=\"{0}\" topLeftCell=\"A{1}\" activePane=\"bottomLeft\" state=\"frozen\"/>", FreezeRows, FreezeRows + 1);
        sheet.Append("</sheetView></sheetViews>");
        if (Widths != null && Widths.Length > 0)
        {
            sheet.Append("<cols>");
            for (int i = 0; i < Widths.Length; i++)
                sheet.AppendFormat(CultureInfo.InvariantCulture, "<col min=\"{0}\" max=\"{0}\" width=\"{1}\" customWidth=\"1\"/>", i + 1, Widths[i]);
            sheet.Append("</cols>");
        }
        sheet.Append("<sheetData>");
        foreach (var r in rows)
        {
            sheet.AppendFormat("<row r=\"{0}\">", r.Key);
            foreach (var c in r.Value) sheet.Append(c.Value);
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData>");
        if (!string.IsNullOrEmpty(FilterRef)) sheet.AppendFormat("<autoFilter ref=\"{0}\"/>", FilterRef);
        sheet.Append("</worksheet>");

        string sn = Esc(sheetName);
        string defName = string.IsNullOrEmpty(FilterRef) ? "" :
            "<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'" + sn + "'!" + AbsRef(FilterRef) + "</definedName></definedNames>";
        var files = new List<KeyValuePair<string, string>>();
        files.Add(new KeyValuePair<string, string>("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>"));
        files.Add(new KeyValuePair<string, string>("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"));
        files.Add(new KeyValuePair<string, string>("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"" + sn + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" + defName + "<calcPr calcId=\"0\" fullCalcOnLoad=\"1\"/></workbook>"));
        files.Add(new KeyValuePair<string, string>("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"));
        files.Add(new KeyValuePair<string, string>("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"0.000\"/><numFmt numFmtId=\"165\" formatCode=\"0.00\"/></numFmts>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF39FF14\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"6\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"165\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyNumberFormat=\"1\"/>" +
            "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>"));
        files.Add(new KeyValuePair<string, string>("xl/worksheets/sheet1.xml", sheet.ToString()));
        File.WriteAllBytes(path, Zip(files));
    }

    static string AbsRef(string r)   // "A6:M20" -> "$A$6:$M$20"
    {
        var sb = new StringBuilder();
        bool prevLetter = false;
        foreach (char ch in r)
        {
            if (char.IsLetter(ch) && !prevLetter) sb.Append('$');
            if (char.IsDigit(ch) && prevLetter) sb.Append('$');
            sb.Append(ch); prevLetter = char.IsLetter(ch);
        }
        return sb.ToString();
    }

    static uint[] crcTable;
    static uint Crc32(byte[] data)
    {
        if (crcTable == null)
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
            crcTable = t;
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    static byte[] Zip(List<KeyValuePair<string, string>> files)
    {
        var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
        var central = new MemoryStream(); var cw = new BinaryWriter(central);
        DateTime now = DateTime.Now;
        ushort dosTime = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        ushort dosDate = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
        foreach (var f in files)
        {
            byte[] name = Encoding.UTF8.GetBytes(f.Key), data = new UTF8Encoding(false).GetBytes(f.Value);
            uint crc = Crc32(data); uint offset = (uint)ms.Position;
            bw.Write(0x04034b50u); bw.Write((ushort)20); bw.Write((ushort)0); bw.Write((ushort)0);
            bw.Write(dosTime); bw.Write(dosDate); bw.Write(crc); bw.Write((uint)data.Length); bw.Write((uint)data.Length);
            bw.Write((ushort)name.Length); bw.Write((ushort)0); bw.Write(name); bw.Write(data);
            cw.Write(0x02014b50u); cw.Write((ushort)20); cw.Write((ushort)20); cw.Write((ushort)0); cw.Write((ushort)0);
            cw.Write(dosTime); cw.Write(dosDate); cw.Write(crc); cw.Write((uint)data.Length); cw.Write((uint)data.Length);
            cw.Write((ushort)name.Length); cw.Write((ushort)0); cw.Write((ushort)0); cw.Write((ushort)0); cw.Write((ushort)0);
            cw.Write(0u); cw.Write(offset); cw.Write(name);
        }
        cw.Flush();
        uint cdOffset = (uint)ms.Position; byte[] cd = central.ToArray();
        bw.Write(cd);
        bw.Write(0x06054b50u); bw.Write((ushort)0); bw.Write((ushort)0); bw.Write((ushort)files.Count); bw.Write((ushort)files.Count);
        bw.Write((uint)cd.Length); bw.Write(cdOffset); bw.Write((ushort)0);
        bw.Flush();
        return ms.ToArray();
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
        int removed = DeletePrevious(work);
        if (removed > 0) lw.WriteLine(string.Format("Önceki çalıştırmadan {0} SEALANT_* nesnesi silindi", removed));

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
                var bi = new BodyInfo(); bi.Name = it.Name; bi.PartNo = it.PartNo; bi.Role = it.Role; bi.TriStart = soup.Count;
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

        string stamp = SafeName(work.Leaf) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string outDir = OutDir(cfg);
        long nDisp = r.DisplayTriangleCount();
        bool willShow = cfg.ShowInNx && nDisp > 0 && nDisp <= cfg.MaxDisplayTriangles;
        if (cfg.WriteExcel)
        {
            try
            {
                string xlsx = Path.Combine(outDir, "sealant_" + stamp + ".xlsx");
                Report.WriteExcel(xlsx, r, bodies, cfg, workToMm, unitName, work.Leaf, willShow);
                lw.WriteLine("EXCEL raporu: " + xlsx);
            }
            catch (Exception ex) { lw.WriteLine("Excel yazılamadı: " + ex.Message); }
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
        if (cfg.WriteStl && nDisp > 0)
        {
            try
            {
                string stl = Path.Combine(outDir, "sealant_" + stamp + ".stl");
                Report.WriteStl(stl, r);
                lw.WriteLine("STL (mm): " + stl + "   -> gerekirse File > Import > STL ile açılabilir");
            }
            catch (Exception ex) { lw.WriteLine("STL yazılamadı: " + ex.Message); }
        }
        if (cfg.ShowInNx && nDisp > 0)
        {
            if (nDisp > cfg.MaxDisplayTriangles)
                lw.WriteLine(string.Format("Gösterim atlandı: {0} üçgen > MaxDisplayTriangles ({1}). SampleSize'ı büyütün veya STL'i kullanın.", nDisp, cfg.MaxDisplayTriangles));
            else
            {
                uf.Ui.SetStatus("Sealant: NX'te gösteriliyor...");
                sw.Restart();
                ShowRegions(work, r, bodies, cfg, workToMm);
                lw.WriteLine(string.Format("Gösterim: {0} üçgen, {1} nesne, renk {2} (fosfor yeşili), layer {3}  ({4:F1} s)  - geri almak için Ctrl+Z",
                    nDisp, r.Display.Count, cfg.SealantColor, cfg.DisplayLayer, sw.Elapsed.TotalSeconds));
                lw.WriteLine("Görmek için: parçaları gizleyin veya saydam yapın (sealant parçaların arasındaki boşlukta durur).");
            }
        }
        uf.Ui.SetStatus("Sealant: tamamlandı");
    }

    static string OutDir(Settings cfg)
    {
        string d = cfg.OutputFolder;
        if (string.IsNullOrEmpty(d)) { try { d = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); } catch { d = ""; } }
        if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) d = Path.GetTempPath();
        return d;
    }

    /// Her parça çifti için bir facet nesnesi: fosfor yeşili, isimli, ayrı layer'da. Tek undo adımı.
    static void ShowRegions(Part work, SealantResult r, List<BodyInfo> bodies, Settings cfg, double workToMm)
    {
        theSession.SetUndoMark(Session.MarkVisibility.Visible, "Sealant gösterimi");
        try { uf.Layer.SetStatus(cfg.DisplayLayer, UFConstants.UF_LAYER_ACTIVE_LAYER); } catch { }
        double inv = 1.0 / workToMm;
        double[,] v = new double[3, 3], nn = new double[3, 3];
        int[] adj = new int[] { UFConstants.UF_FACET_NULL_FACET_ID, UFConstants.UF_FACET_NULL_FACET_ID, UFConstants.UF_FACET_NULL_FACET_ID };
        int idx = -1;
        int color = cfg.SealantColor;
        lw.WriteLine("---- Gösterim nesneleri (Excel'deki 'NX Nesnesi' sütunu ile aynı) ----");
        foreach (var kv in Report.SortedPairs(r))
        {
            idx++;
            List<float> d;
            if (!r.Display.TryGetValue(kv.Key, out d) || d.Count < 9) continue;
            string name = Report.PairObjectName(idx);
            try
            {
                Tag model;
                uf.Facet.CreateModel(work.Tag, out model);
                for (int i = 0; i + 8 < d.Count; i += 9)
                {
                    V3 a = new V3(d[i], d[i + 1], d[i + 2]), b = new V3(d[i + 3], d[i + 4], d[i + 5]), c = new V3(d[i + 6], d[i + 7], d[i + 8]);
                    V3 n = V3.Cross(b - a, c - a).Unit();
                    for (int k = 0; k < 3; k++)
                    {
                        v[k, 0] = d[i + 3 * k] * inv; v[k, 1] = d[i + 3 * k + 1] * inv; v[k, 2] = d[i + 3 * k + 2] * inv;
                        nn[k, 0] = n.X; nn[k, 1] = n.Y; nn[k, 2] = n.Z;
                    }
                    int fid; uf.Facet.AddFacetToModel(model, 3, v, nn, adj, out fid);
                }
                uf.Facet.ModelEditsDone(model);
                uf.Obj.SetName(model, name);
                uf.Obj.SetColor(model, color);
                uf.Obj.SetLayer(model, cfg.DisplayLayer);
                try { uf.Disp.AddItemToDisplay(model); } catch { }
                int a2 = (int)(kv.Key >> 32), b2 = (int)(kv.Key & 0xffffffffL);
                lw.WriteLine(string.Format("  {0}  {1,9} g  {2}  <->  {3}", name,
                    Report.Fmt(kv.Value.Vol * cfg.DensityGcm3 * 1e-3, 2), bodies[a2].PartNo, bodies[b2].PartNo));
            }
            catch (Exception ex) { lw.WriteLine("  " + name + " oluşturulamadı: " + ex.Message); }
        }
        try { uf.Disp.RegenerateDisplay(); } catch { }
    }

    /// Önceki çalıştırmanın work part'ta bıraktığı SEALANT_* facet nesnelerini ve CG noktasını siler.
    static int DeletePrevious(Part work)
    {
        var del = new List<Tag>();
        foreach (int type in new int[] { UFConstants.UF_faceted_model_type, UFConstants.UF_point_type })
        {
            Tag t = Tag.Null;
            while (true)
            {
                uf.Obj.CycleObjsInPart(work.Tag, type, ref t);
                if (t == Tag.Null) break;
                string nm = "";
                try { uf.Obj.AskName(t, out nm); } catch { continue; }
                if (nm != null && nm.StartsWith("SEALANT_", StringComparison.OrdinalIgnoreCase)) del.Add(t);
            }
        }
        foreach (Tag t in del) { try { uf.Obj.DeleteObject(t); } catch { } }
        return del.Count;
    }

    public static int GetUnloadOption(string dummy) { return (int)Session.LibraryUnloadOption.Immediately; }

    // ------------------------------------------------------------------------------------
    sealed class BodyItem
    {
        public Body Occ, Proto; public Component Comp; public string Name; public string PartNo; public BodyRole Role;
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
                    || Report.StartsWithAny(c.DisplayName, cfg.ExcludePrefixes)
                    || Report.StartsWithAny(c.Name, cfg.ExcludePrefixes)
                    || Report.StartsWithAny(partName, cfg.ExcludePrefixes)
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
        if (compExcluded || Report.MatchesAny(bodyName, cfg.ExcludeKeywords) || Report.StartsWithAny(bodyName, cfg.ExcludePrefixes)
            || HasAttr(b, cfg.ExcludeAttribute)) return BodyRole.Excluded;
        return BodyRole.Normal;
    }

    static void Collect(List<BodyItem> items, Body occ, Body proto, Component c, Part protoPart, BodyRole role, Settings cfg)
    {
        var it = new BodyItem();
        it.Occ = occ; it.Proto = proto; it.Comp = c; it.Role = role;
        it.ProtoToMm = protoPart.PartUnits == BasePart.Units.Inches ? 25.4 : 1.0;
        string bn = string.IsNullOrEmpty(proto.Name) ? proto.JournalIdentifier : proto.Name;
        it.Name = (c != null ? CompPath(c) : "<root>") + "/" + bn;
        it.PartNo = PartNo(protoPart, cfg);
        items.Add(it);
    }

    /// Parça numarası: önce PartNoAttribute (Teamcenter: DB_PART_NO), yoksa part adının "/" öncesi
    static string PartNo(Part p, Settings cfg)
    {
        if (p == null) return "";
        if (!string.IsNullOrEmpty(cfg.PartNoAttribute))
        {
            try
            {
                if (p.HasUserAttribute(cfg.PartNoAttribute, NXObject.AttributeType.String, -1))
                {
                    string v = p.GetUserAttributeAsString(cfg.PartNoAttribute, NXObject.AttributeType.String, -1);
                    if (!string.IsNullOrEmpty(v)) return v.Trim();
                }
            }
            catch { }
        }
        string leaf = p.Leaf ?? "";
        int k = leaf.IndexOf('/');
        return k > 0 ? leaf.Substring(0, k) : leaf;
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
