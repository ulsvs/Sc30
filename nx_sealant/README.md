# NX Sealant Hacim / Ağırlık / CG Makrosu

`SealantCG.cs` — NX Open C# journal. Montajdaki parçalar arası boşluklara atılacak
sealant'ın **hacmini, ağırlığını, CG'sini ve CG etrafındaki ataletini** hesaplar.
Bağlantı bölgeleri (menteşe, cıvata, burç vb. ve çevresindeki emniyet payı) hariç tutulur.

## Kullanım
1. Montajı açın; Displayed part = Work part olsun.
2. `File > Execute > NX Open...` (veya Alt+F8) → `SealantCG.cs`.
3. Açılan seçim penceresinde ek olarak hariç tutulacak bileşen/gövdeleri seçin (yoksa OK).
4. Sonuç Listing Window'da; parça çifti bazında CSV `%TEMP%` altına yazılır;
   CG'ye `SEALANT_CG` isimli nokta konur.
5. **Sealant bölgeleri montajda gösterilir:** her parça çifti için boşluğun orta
   düzleminde renkli bir facet nesnesi (`SEALANT_001`, `SEALANT_002`, ...) layer 251'e
   eklenir. Listing Window hangi nesnenin hangi parça çiftine ait olduğunu ve ağırlığını
   yazar. Parçaları gizleyin veya saydam yapın, sealant bölgeleri görünür. Tek adımda
   Ctrl+Z ile geri alınır; makro tekrar çalışınca eski `SEALANT_*` nesneleri silinir.
   Component part'lar değiştirilmez. Aynı bölgeler `%TEMP%` altına STL olarak da yazılır.

## Ayarlar (dosyanın başındaki `Settings` sınıfı)
| Ayar | Varsayılan | Anlamı |
|---|---|---|
| `MinGap` / `MaxGap` | 0.1 / 3.0 mm | Bu aralıktaki boşluklar doldurulur (altı temas, üstü açıklık) |
| `MaxAngleDeg` | 25° | Yüzeylerin "karşılıklı" sayılması için açı toleransı |
| `SampleSize` | 1.0 mm | Örnekleme aralığı (küçük = hassas/yavaş) |
| `DensityGcm3` | 1.55 | Sealant yoğunluğu — TDS'den girin |
| `ExcludeKeywords` | HINGE, MENTESE, BOLT, ... | Adında geçen bileşenler bağlantı elemanı sayılır |
| `ExcludeAttribute` | `SEALANT_EXCLUDE` | Bu attribute'a sahip bileşen/part/gövde hariç |
| `ExcludeMargin` | 10 mm | Hariç parçaların çevresinde sealant'sız bırakılan mesafe |
| `KeepOutName` / `KeepOutLayer` | `SEAL_KEEPOUT` / 250 | Kullanıcının çizdiği "yasak hacim" solidleri |
| `ShowInNx` / `DisplayLayer` | true / 251 | Sealant bölgelerini montajda göster / layer'ı |
| `MaxDisplayTriangles` | 3 000 000 | Üstündeyse NX'te çizilmez, sadece STL yazılır |
| `WriteStl` | true | Sealant bölgelerini STL olarak kaydet |

## Test (NX gerekmez)
```
cd nx_sealant/test && dotnet run -c Release
```
Çekirdek algoritmayı sentetik geometriyle (paralel plaka, temas, menteşe payı, keep-out,
kama boşluk, 4800 gövdelik performans modeli) doğrular. C# 5 dil seviyesinde derlenir.
