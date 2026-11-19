// NX olmadan çekirdek algoritma testi:  dotnet run -c Release  (nx_sealant/test içinde)
using System;
using System.Collections.Generic;
using System.Diagnostics;

public static class Program
{
    static int fails = 0;

    static void AddBox(TriSoup s, List<BodyInfo> bodies, string name, BodyRole role,
                       double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var bi = new BodyInfo(); bi.Name = name; bi.Role = role; bi.TriStart = s.Count;
        V3[] c = new V3[8];
        for (int i = 0; i < 8; i++) c[i] = new V3((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? z0 : z1);
        int[][] quads = new int[][] {
            new int[] {0,2,3,1}, new int[] {4,5,7,6},   // -Z, +Z
            new int[] {0,1,5,4}, new int[] {2,6,7,3},   // -Y, +Y
            new int[] {0,4,6,2}, new int[] {1,3,7,5} }; // -X, +X
        foreach (int[] q in quads)
        {
            V3 a = c[q[0]], b = c[q[1]], cc = c[q[2]], d = c[q[3]];
            V3 n = V3.Cross(b - a, cc - a).Unit();
            s.Add(a, b, cc, n, bodies.Count); s.Add(a, cc, d, n, bodies.Count);
        }
        bi.TriCount = s.Count - bi.TriStart;
        bi.Min = new V3(x0, y0, z0); bi.Max = new V3(x1, y1, z1);
        bodies.Add(bi);
    }

    static void Check(string what, double got, double exp, double relTol)
    {
        bool ok = Math.Abs(got - exp) <= relTol * Math.Max(1.0, Math.Abs(exp));
        if (!ok) fails++;
        Console.WriteLine("{0,-4} {1,-45} beklenen {2,12:F3}  bulunan {3,12:F3}", ok ? "OK" : "FAIL", what, exp, got);
    }

    static double DispArea(SealantResult r, out double minZ, out double maxZ)
    {
        double A = 0; minZ = double.MaxValue; maxZ = -double.MaxValue;
        foreach (var kv in r.Display)
            for (int i = 0; i + 8 < kv.Value.Count; i += 9)
            {
                var d = kv.Value;
                V3 a = new V3(d[i], d[i + 1], d[i + 2]), b = new V3(d[i + 3], d[i + 4], d[i + 5]), c = new V3(d[i + 6], d[i + 7], d[i + 8]);
                A += 0.5 * V3.Cross(b - a, c - a).Len();
                minZ = Math.Min(minZ, Math.Min(a.Z, Math.Min(b.Z, c.Z))); maxZ = Math.Max(maxZ, Math.Max(a.Z, Math.Max(b.Z, c.Z)));
            }
        return A;
    }

    public static int Main()
    {
        var cfg = new Settings(); cfg.MinGap = 0.1; cfg.MaxGap = 3; cfg.SampleSize = 1; cfg.FacetSize = 8; cfg.ExcludeMargin = 10;

        // 1) İki plaka, 1 mm boşluk, 50x100 mm örtüşme
        {
            var s = new TriSoup(cfg.FacetSize); var b = new List<BodyInfo>();
            AddBox(s, b, "A", BodyRole.Normal, 0, 0, 0, 100, 100, 2);
            AddBox(s, b, "B", BodyRole.Normal, 50, 0, 3, 150, 100, 5);
            var r = SealantSolver.Run(s, b, cfg);
            V3 cg = r.Total.Cg();
            Check("1 hacim mm3", r.Total.Vol, 5000, 0.01);
            Check("1 CG x", cg.X, 75, 0.002); Check("1 CG y", cg.Y, 50, 0.002); Check("1 CG z", cg.Z, 2.5, 0.002);
            Check("1 kütle kg (1.55 g/cm3)", r.MassKg, 5000 * 1.55e-6, 0.01);
            double z0, z1; double da = DispArea(r, out z0, out z1);
            Check("1 gösterim alanı = sealant alanı", da, r.Total.Area, 0.001);
            Check("1 gösterim orta düzlemde (z min)", z0, 2.5, 0.001);
            Check("1 gösterim orta düzlemde (z max)", z1, 2.5, 0.001);
            string stl = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sealant_test.stl");
            Report.WriteStl(stl, r);
            Check("1 STL boyutu", new System.IO.FileInfo(stl).Length, 84 + 50 * r.DisplayTriangleCount(), 0);
            // İndeks sırası ters olsa da aynı sonuç (çift sayım yok)
            var s2 = new TriSoup(cfg.FacetSize); var b2 = new List<BodyInfo>();
            AddBox(s2, b2, "B", BodyRole.Normal, 50, 0, 3, 150, 100, 5);
            AddBox(s2, b2, "A", BodyRole.Normal, 0, 0, 0, 100, 100, 2);
            Check("1b ters sıra hacim", SealantSolver.Run(s2, b2, cfg).Total.Vol, 5000, 0.01);
        }
        // 2) Temas (0 boşluk) ve büyük açıklık (> MaxGap) sealant almaz
        {
            var s = new TriSoup(cfg.FacetSize); var b = new List<BodyInfo>();
            AddBox(s, b, "A", BodyRole.Normal, 0, 0, 0, 100, 100, 2);
            AddBox(s, b, "C", BodyRole.Normal, 0, 0, -2, 100, 100, 0);   // temas
            AddBox(s, b, "D", BodyRole.Normal, 0, 0, 6, 100, 100, 8);    // 4 mm açıklık
            Check("2 temas + büyük açıklık hacim", SealantSolver.Run(s, b, cfg).Total.Vol, 0, 0.001);
        }
        // 3) Menteşe (hariç) + 10 mm emniyet payı
        {
            var s = new TriSoup(cfg.FacetSize); var b = new List<BodyInfo>();
            AddBox(s, b, "A", BodyRole.Normal, 0, 0, 0, 100, 100, 2);
            AddBox(s, b, "B", BodyRole.Normal, 50, 0, 3, 150, 100, 5);
            AddBox(s, b, "HINGE", BodyRole.Excluded, 90, 40, 5.5, 110, 60, 8);
            double r0 = Math.Sqrt(100 - 9);  // orta düzlem z=2.5, menteşe alt yüzü z=5.5 => yatay yarıçap
            double removed = 10 * (20 + 2 * r0) + 20 * r0 + Math.PI * r0 * r0 / 2;
            var r = SealantSolver.Run(s, b, cfg);
            Check("3 menteşe payı düşülmüş hacim", r.Total.Vol, 5000 - removed, 0.01);
            Check("3 menteşe-B çifti yok (çift sayısı)", r.Pairs.Count, 1, 0);
            double z0, z1;
            Check("3 gösterim alanı = sealant alanı", DispArea(r, out z0, out z1), r.Total.Area, 0.001);
            Check("3 gösterim nesnesi sayısı", r.Display.Count, 1, 0);
        }
        // 4) Keep-out gövdesi
        {
            var s = new TriSoup(cfg.FacetSize); var b = new List<BodyInfo>();
            AddBox(s, b, "A", BodyRole.Normal, 0, 0, 0, 100, 100, 2);
            AddBox(s, b, "B", BodyRole.Normal, 50, 0, 3, 150, 100, 5);
            AddBox(s, b, "SEAL_KEEPOUT", BodyRole.KeepOut, 55, 0, 1, 65, 10, 4);
            Check("4 keep-out düşülmüş hacim", SealantSolver.Run(s, b, cfg).Total.Vol, 4900, 0.01);
        }
        // 5) Eğik boşluk (kama): üst plaka 2° eğik -> ışın boyu ortalama boşluk
        {
            var s = new TriSoup(cfg.FacetSize); var b = new List<BodyInfo>();
            AddBox(s, b, "A", BodyRole.Normal, 0, 0, 0, 60, 60, 2);
            var bi = new BodyInfo(); bi.Name = "W"; bi.Role = BodyRole.Normal; bi.TriStart = s.Count;
            // alt yüzü z = 2.5 + x*tan(2°) olan tek yüzlü "kapak" (sadece alt yüz yeterli)
            double k = Math.Tan(2 * Math.PI / 180);
            V3 p0 = new V3(0, 0, 2.5), p1 = new V3(60, 0, 2.5 + 60 * k), p2 = new V3(60, 60, 2.5 + 60 * k), p3 = new V3(0, 60, 2.5);
            V3 n = V3.Cross(p3 - p0, p1 - p0).Unit();
            s.Add(p0, p3, p2, n, 1); s.Add(p0, p2, p1, n, 1);
            bi.TriCount = s.Count - bi.TriStart; bi.Min = new V3(0, 0, 2.5); bi.Max = new V3(60, 60, 2.5 + 60 * k); b.Add(bi);
            // boşluk 0.5 .. 2.595 => tümü MaxGap içinde; hacim = 60*60*(0.5 + 30k)
            Check("5 kama hacim", SealantSolver.Run(s, b, cfg).Total.Vol, 3600 * (0.5 + 30 * k), 0.01);
        }
        // 6) Performans: 40x40 ızgara panel + her birine 1 mm boşlukla bağlı 2 braket (4800 gövde)
        {
            var s = new TriSoup(cfg.FacetSize); var b = new List<BodyInfo>();
            int nx = 40, ny = 40; double exp = 0;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    double x = i * 210, y = j * 210;
                    AddBox(s, b, "P", BodyRole.Normal, x, y, 0, x + 200, y + 200, 2);
                    AddBox(s, b, "K1", BodyRole.Normal, x + 10, y + 10, 3, x + 90, y + 190, 20);
                    AddBox(s, b, "K2", BodyRole.Normal, x + 110, y + 10, 2.5, x + 190, y + 190, 20);
                    exp += 80 * 180 * 1.0 + 80 * 180 * 0.5;
                }
            var sw = Stopwatch.StartNew();
            var r = SealantSolver.Run(s, b, cfg);
            Console.WriteLine("     6: gösterim üçgeni {0}", r.DisplayTriangleCount());
            Console.WriteLine("     6: {0} gövde, {1} üçgen, {2} aday üçgen, {3} örnek -> {4:F2} s ({5} çekirdek)",
                b.Count, s.Count, r.CandidateTriangles, r.Total.Samples, sw.Elapsed.TotalSeconds, Environment.ProcessorCount);
            Check("6 performans modeli toplam hacim", r.Total.Vol, exp, 0.01);
            foreach (string l in Report.Summary(r, cfg, 1.0, "mm")) Console.WriteLine("     " + l);
        }
        Console.WriteLine(fails == 0 ? "TÜM TESTLER GEÇTİ" : fails + " TEST BAŞARISIZ");
        return fails == 0 ? 0 : 1;
    }
}
