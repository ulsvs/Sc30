import sys
from pathlib import Path

import pytest
from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import bom_agirlik_fark as baf  # noqa: E402

ORNEK = ROOT / "ornekler"


def test_ornek_dosyalar(tmp_path):
    out = tmp_path / "fark.xlsx"
    assert baf.main([str(ORNEK / "agirlik_eski.xlsx"), str(ORNEK / "agirlik_yeni.xlsx"), "-o", str(out)]) == 0
    wb = load_workbook(out)
    assert {"Özet", "Farklar", "Bilgi Değişiklikleri", "Tüm Parçalar"} <= set(wb.sheetnames)
    farklar = {r[1]: r for r in wb["Farklar"].iter_rows(min_row=2, values_only=True)}
    assert farklar["SC30-110"][0] == baf.CHANGED and farklar["SC30-110"][18] == pytest.approx(23.0)
    assert farklar["SC30-120"][14] == pytest.approx(0.4)          # sadece fastener değişti
    assert farklar["SC30-160"][0] == baf.ADDED
    assert farklar["SC30-140"][0] == baf.REMOVED
    bilgi = {r[1]: r[-1] for r in wb["Bilgi Değişiklikleri"].iter_rows(min_row=2, values_only=True)}
    assert "KABİN SACI → KABİN PLAKASI" in bilgi["SC30-130"]


def test_toplamlar_ve_aggregate():
    over = dict.fromkeys(("parca", "isim", "agirlik", "fastener", "toplam", "adet", "seviye"))
    ob = baf.load_bom(ORNEK / "agirlik_eski.xlsx", None, over, None)
    nb = baf.load_bom(ORNEK / "agirlik_yeni.xlsx", None, over, None)
    ow, of_, ot, m = baf.bom_totals(ob, "auto", False)
    nw, nf, nt, _ = baf.bom_totals(nb, "auto", False)
    assert m == "yaprak"
    assert (nt - ot) == pytest.approx(25.2)
    assert (nf - of_) == pytest.approx(1.1)
    diffs = baf.compare(baf.aggregate(ob, False), baf.aggregate(nb, False), 0.001)
    assert sum(d.delta("t") for d in diffs) == pytest.approx(nt - ot)


def test_csv_ondalik_virgul_ve_tekrar_eden_parca(tmp_path):
    (tmp_path / "e.csv").write_text("Part No;Weight;Fastener Wt\nP1;1,5;0,1\nP1;1,5;0,1\nP2;2;0\n", encoding="utf-8")
    (tmp_path / "y.csv").write_text("Part No;Weight;Fastener Wt\nP1;1,5;0,1\nP2;2,25;0\nTOPLAM;3,75;0,1\n",
                                    encoding="utf-8")
    over = dict.fromkeys(("parca", "isim", "agirlik", "fastener", "toplam", "adet", "seviye"))
    o = baf.aggregate(baf.load_bom(tmp_path / "e.csv", None, over, None), False)
    n = baf.aggregate(baf.load_bom(tmp_path / "y.csv", None, over, None), False)
    d = {x.key: x for x in baf.compare(o, n, 0.001)}
    assert "TOPLAM" not in d
    assert d["P1"].delta("t") == pytest.approx(-1.6)   # bir kopyası kalktı
    assert d["P2"].delta("w") == pytest.approx(0.25)


@pytest.mark.parametrize("s,v", [("1.234,5", 1234.5), ("1,234.5", 1234.5), ("12,4", 12.4), ("3 kg", 3.0), ("", None)])
def test_parse_num(s, v):
    assert baf.parse_num(s) == v
