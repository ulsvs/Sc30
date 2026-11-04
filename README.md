# Ağırlık BOM karşılaştırma (Excel macro çıktısı) — `bom_agirlik_fark.py`

Aynı formattaki iki ağırlık BOM Excel dosyasını karşılaştırır; tek dosyalık,
sadece `openpyxl` gerektirir.

```
pip install openpyxl
python bom_agirlik_fark.py eski.xlsx yeni.xlsx            # rapor: agirlik_fark_eski_yeni.xlsx
python bom_agirlik_fark.py                                 # dosyaları sorar (sürükle-bırak)
python bom_agirlik_fark.py ornekler/agirlik_eski.xlsx ornekler/agirlik_yeni.xlsx
```

Ekrana toplam / parça / fastener ağırlık farkını ve en çok etki eden parçaları
yazar; Excel raporunda:

- **Özet:** eski-yeni ağırlık, fastener, toplam ve farkları; durum sayıları
- **Farklar:** değişen / eklenen / silinen parçalar, toplam farka göre sıralı
  (adet, ağırlık, fastener, toplam: eski · yeni · fark · fark %)
- **Bilgi Değişiklikleri:** ağırlığı aynı ama isim/revizyon vb. değişenler
- **Tüm Parçalar:** hepsi; filtre açık

Notlar:
- Başlık satırı ve sayfa otomatik bulunur (üstte başlık/logo satırları olabilir).
  Sütunlar TR/EN başlıklardan tanınır; tanınmazsa
  `--parca "Part No" --agirlik "Weight" --fastener "Fastener Wt" --toplam "Total"`
  ile verin. `--sayfa` ile sayfa seçilebilir.
- Eşleştirme parça numarasıyla yapılır; aynı parça birden çok satırdaysa
  değerleri toplanır. Farklı anahtar için `--anahtar "Parent" "Part No"`.
- Toplam sütunu yoksa toplam = ağırlık + fastener. Ağırlıklar birim ağırlıksa
  `--adet-carp`.
- BOM toplamı: seviye sütunu varsa en alt seviye satırların toplamı (montaj
  satırları çift sayılmasın diye), yoksa tüm satırlar; `--toplam-yontemi
  hepsi|yaprak|kok` ile değiştirilebilir. "TOPLAM" satırları ve parça no'su boş
  satırlar atlanır.
- `--tolerans 0.001` (kg) altındaki farklar yok sayılır.

---

# Teamcenter Revizyon Karşılaştırma (ağırlık · konum · isimlendirme)

Eski bir revizyon numarasını girersiniz; araç Teamcenter'dan o revizyonun ve
**en güncel** revizyonun BOM'unu tüm seviyeleriyle açar, satırları eşleştirir
ve neyin değiştiğini özetler:

- **Ağırlık:** toplam ağırlık farkı, ağırlığı değişen parçalar ve toplama en çok
  etki eden değişiklikler (eklenen/silinen alt montajlar dahil)
- **Konum:** yer değiştiren (mm) ve dönen (°) parçalar, eski/yeni X-Y-Z
- **İsimlendirme:** ismi değişen parçalar, farklı Item ID ile değiştirilmiş parçalar
- Eklenen / silinen satırlar

Turuncu-kırmızı satırları modelde tek tek açmak yerine hepsi tek bir Excel
raporunda listelenir (turuncu = değişen, kırmızı = silinen, yeşil = eklenen,
sarı = başka parçayla değiştirilen).

## Kurulum

```
pip install -r requirements.txt          # Python 3.11+
copy ayarlar.ornek.toml ayarlar.toml     # (Linux: cp) ve düzenleyin
```

`ayarlar.toml` içinde en az `[teamcenter] url` ve `[ozellikler] agirlik`
ayarlanmalı.

## Kullanım

```
python -m tc_rev_compare 123456/A
```

Çıktı örneği:

```
Eski: 100000/A  GÖVDE MONTAJI  (oluşturma 2021-03-01, yayın 2021-04-12)
Yeni: 100000/F  GÖVDE MONTAJI  (oluşturma 2026-09-10, yayın -)

Toplam ağırlık : 929.950 kg  ->  954.550 kg   Fark: +24.600 kg (+2.65%)
BOM satırı     : 9  ->  10

Değişiklik özeti
  Ağırlığı değişen          : 3
  Konumu/açısı değişen      : 2
  İsmi değişen              : 1
  Eklenen                   : 1
  Silinen                   : 0
  Değiştirilen (farklı parça): 1

Ağırlığa en çok etki eden 5 değişiklik
     +22.500 kg  ağırlık                100100/C               ŞASİ KAYNAKLI
      +4.800 kg  EKLENDİ                100700/A               AKÜ BRAKETİ
      -1.700 kg  DEĞİŞTİRİLDİ           100600/A               KABLO KANALI ALÜMİNYUM
...
Ayrıntılı rapor: karsilastirma_100000_A_F.xlsx
```

Diğer seçenekler:

| Komut | Açıklama |
|---|---|
| `python -m tc_rev_compare A` | `varsayilan_item_id` ayarlıysa sadece revizyon |
| `--yeni-rev D` | en güncel yerine belirli bir revizyonla karşılaştır |
| `--hedef yayinlanmis` | en son *released* revizyonla karşılaştır |
| `--eski-tarih 2021-04-12` | eski BOM'u bu tarihe göre konfigüre et (tarihli revizyon kuralı ile) |
| `--ozellik-test bl_rev_weight bl_rev_mass` | özelliklerin dolu olup olmadığını gösterir |
| `--dosya eski.xlsx yeni.xlsx` | Teamcenter yerine Structure Manager export'larını karşılaştır |
| `--cikti rapor.xlsx`, `--ilk 20`, `-v` | rapor yolu, özet satır sayısı, ayrıntılı log |

Şifre her seferinde sorulur; `TC_USER` / `TC_PASSWORD` ortam değişkenleri de kullanılabilir.

## İlk kurulumda kontrol edilecekler

1. **Ağırlık özelliği:** Firmanızda ağırlık hangi özellikte tutuluyor
   (NX mass senkronizasyonu, özel bir attribute, IRM formu...)? Structure
   Manager'da ağırlığı gördüğünüz sütunun iç adını bulun (BOM satırında genelde
   `bl_rev_<attribute>` biçiminde) ve deneyin:
   `python -m tc_rev_compare 123456/A --ozellik-test bl_rev_weight bl_rev_mass bl_rev_xx_agirlik`
   Dolu çıkanı `[ozellikler] agirlik` listesine yazın. Birimi gram ise
   `agirlik_carpani = 0.001`.
2. **Revizyon kuralı:** Eski revizyonun BOM'u açılırken alt parçaların hangi
   revizyonlarının geleceğini kural belirler. Teamcenter'da karşılaştırırken
   kullandığınız kuralı `revizyon_kurali` olarak yazın. Yapı *precise* değilse
   eski BOM için tarihli bir kural (`eski_revizyon_kurali`) ve `--eski-tarih` kullanın.
3. **Paketleme (pack):** Aynı parçanın kopyaları paketli gelirse konumları tek
   satıra iner. `PSEAutoPackPref` tercihini kapatmanız önerilir; paketli satırların
   ağırlığı `bl_pack_count` ile çarpılarak toplama katılır.
4. **SSO:** Teamcenter girişiniz SSO (TCSS) ile ise kullanıcı/şifre ile
   JSON-REST girişi çalışmayabilir; bu durumda `--dosya` modunu kullanın veya
   SSO desteği eklenmesi gerekir.

## Dosya modu (Teamcenter bağlantısı olmadan)

Structure Manager'dan iki revizyonu **tüm seviyeler açık** halde Excel/CSV'ye
aktarın. Gerekli sütunlar `Seviye` ve `Item ID`; varsa `Revizyon`, `İsim`,
`Find No`, `Ağırlık`, `Adet` ve konum için `X`/`Y`/`Z` (mm) veya 16 değerli
`Dönüşüm` (`bl_plmxml_occ_xform`) kullanılır. Farklı başlıklar
`[dosya_sutunlari]` ile eşlenebilir. `ornekler/` klasöründe örnek var:

```
python -m tc_rev_compare --dosya ornekler/eski.csv ornekler/yeni.csv
```

## Nasıl eşleştiriyor?

- Ağaç yukarıdan aşağı eşleştirilir; eşleşen iki montajın çocukları Item ID'ye
  göre gruplanır.
- Aynı parçanın birden fazla kopyası varsa önce Find No, sonra **en yakın lokal
  konum** ile eşleştirilir (sıra değişse bile yanlış "yer değiştirdi" demez).
- Eşleşmeyen satırlardan aynı Find No'da veya aynı konumda (`degistirme_mm`)
  olanlar "değiştirildi", diğerleri eklendi/silindi sayılır.
- Konum farkı ebeveyne göre (lokal) ölçülür; böylece bir alt montaj taşındığında
  altındaki her parça ayrıca "taşındı" görünmez. Raporda mutlak X-Y-Z de verilir.
- Toplam ağırlık parça (yaprak) ağırlıklarının toplamıdır; alt parçalarında
  ağırlık olmayan montajlarda montajın kendi ağırlığı kullanılır.

## Teamcenter tarafı (teknik)

SOA JSON-REST (`<url>/JsonRestServices/...`) servisleri kullanılır:
`Core-2011-06-Session/login`, `Core-2007-01-DataManagement/getItemFromId`,
`Core-2006-03-DataManagement/getProperties`,
`Cad-2007-01-StructureManagement/createBOMWindows`,
`Cad-2008-06-StructureManagement/expandPSAllLevels`, `closeBOMWindows`.
Sadece okuma yapar, Teamcenter'da hiçbir şeyi değiştirmez.

## Test

```
pip install pytest
python -m pytest -q
```
