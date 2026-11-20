# NX Sealant Hacim / Ağırlık / CG Makrosu

`SealantCG.cs` — NX Open C# journal. Montajdaki parçalar arası boşluklara atılacak
sealant'ın **hacmini, ağırlığını, CG'sini ve CG etrafındaki ataletini** hesaplar.
Bağlantı bölgeleri (menteşe, cıvata, burç vb. ve çevresindeki emniyet payı) hariç tutulur.

## Kullanım
1. Montajı açın; Displayed part = Work part olsun.
2. `File > Execute > NX Open...` (veya Alt+F8) → `SealantCG.cs`.
3. Açılan seçim penceresinde ek olarak hariç tutulacak bileşen/gövdeleri seçin (yoksa OK).
4. Sonuç Listing Window'da yazılır; CG'ye `SEALANT_CG` isimli nokta konur.
5. **Sealant bölgeleri montajda fosfor yeşili görünür:** her parça çifti için boşluğun orta
   düzleminde bir facet nesnesi (`SEALANT_001`, `SEALANT_002`, ...) layer 251'e eklenir.
   Parçaları gizleyin veya saydam yapın. Tek adımda Ctrl+Z ile geri alınır; makro tekrar
   çalışınca eski `SEALANT_*` nesneleri silinir. Component part'lar değiştirilmez.
6. **Excel raporu** (Masaüstü, `sealant_<montaj>_<tarih>.xlsx`): her satır karşılıklı iki
   parça — Parça No 1, Parça No 2, Yüzey Alanı, CGx/CGy/CGz, Sealant Ağırlık, ayrıca hacim,
   ortalama kalınlık, temas alanı, NX nesne adı ve montaj yolları. Ağırlık = Hacim × B2
   (yoğunluk) formülüdür; B2 değişince yeniden hesaplanır. Üstte toplam ağırlık ve CG.

## Ayarlar (dosyanın başındaki `Settings` sınıfı)
| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `MinGap` / `MaxGap` | 0.1 / 3.0 mm | Bu aralıktaki boşluklar doldurulur (altı temas, üstü açıklık) |
| `MaxAngleDeg` | 25° | Yüzeylerin "karşılıklı" sayılması için açı toleransı |
| `IncludeFaying` / `FayingThickness` | true / 0.10 mm | Birbirine değen yüzeylere bu kalınlıkta sealant say (raporda ayrı) |
| `SampleSize` | 1.0 mm | Örnekleme aralığı (küçük = hassas/yavaş) |
| `DensityGcm3` | 1.55 | Ağırlık katsayısı (sealant yoğunluğu, g/cm³) — TDS'den girin |
| `SealantColor` | 30 | Gösterim rengi (30 = fosfor yeşili) |
| `OutputFolder` | boş (Masaüstü) | Excel ve STL'in kaydedileceği klasör |
| `PartNoAttribute` | `DB_PART_NO` | Parça numarasının okunacağı attribute |
| `ExcludeKeywords` | HINGE, MENTESE, BOLT, ... | Adında geçen bileşenler bağlantı elemanı sayılır |
| `ExcludeAttribute` | `SEALANT_EXCLUDE` | Bu attribute'a sahip bileşen/part/gövde hariç |
| `ExcludeMargin` | 10 mm | Hariç parçaların çevresinde sealant'sız bırakılan mesafe |
| `KeepOutName` / `KeepOutLayer` | `SEAL_KEEPOUT` / 250 | Kullanıcının çizdiği "yasak hacim" solidleri |
| `ShowInNx` / `DisplayLayer` | true / 251 | Sealant bölgelerini montajda göster / layer'ı |
| `MaxDisplayTriangles` | 3 000 000 | Üstündeyse NX'te çizilmez, sadece STL yazılır |
| `WriteStl` | true | Sealant bölgelerini STL olarak kaydet |

## Hızlı çalışma için
`SampleSize` 2.0, `FacetChordTol` 0.05, `FacetSize` 15, `ExcludeMargin` 3,
gerekmiyorsa `ShowInNx = false`. Ayrıntılar `Settings` sınıfının başındaki yorumda.

## Test (NX gerekmez)
```
cd nx_sealant/test && dotnet run -c Release
```
Çekirdek algoritmayı sentetik geometriyle (paralel plaka, temas, menteşe payı, keep-out,
kama boşluk, 4800 gövdelik performans modeli) doğrular. C# 5 dil seviyesinde derlenir.
