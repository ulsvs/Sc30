#!/usr/bin/env python3
"""İki ağırlık BOM dosyasını (Excel macro çıktısı, aynı format) karşılaştırır.

Her parça için ağırlık, fastener ağırlığı, toplam ağırlık ve adet farkını,
ayrıca eklenen / silinen parçaları ve isim/revizyon gibi diğer sütunlardaki
değişiklikleri bir Excel raporuna yazar.

Kullanım:
    python bom_agirlik_fark.py eski.xlsx yeni.xlsx
    python bom_agirlik_fark.py eski.xlsx yeni.xlsx -o fark.xlsx --sayfa "BOM"
    python bom_agirlik_fark.py            (dosya yolları sorulur; sürükle-bırak olur)

Sütunlar başlıklardan otomatik bulunur (TR/EN). Bulunamazsa elle verin:
    --parca "Part No" --agirlik "Weight" --fastener "Fastener Wt" --toplam "Total Wt"

Gerekli: Python 3.9+, openpyxl  (pip install openpyxl)
"""

from __future__ import annotations

import argparse
import csv
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

# ----------------------------------------------------------------------------
# Sütun tanıma
# ----------------------------------------------------------------------------

_TR = str.maketrans("ıİğĞüÜşŞöÖçÇ", "iigguussoocc")


def norm(s) -> str:
    """Başlık karşılaştırması için: küçük harf, Türkçe karakter ve boşluk/işaret yok."""
    s = str(s if s is not None else "").translate(_TR).casefold()
    s = s.replace("i̇", "i")
    return re.sub(r"[^a-z0-9]", "", s)


WEIGHT_WORDS = ("agirlik", "weight", "mass", "kutle", "kg")

# (alan, tam eşleşen adlar, içinde geçen kelimeler) — sıra önemli: özel olanlar önce
ALIASES: list[tuple[str, tuple[str, ...], tuple[str, ...]]] = [
    ("parca", ("partno", "partnumber", "parcano", "parcanumarasi", "parcakodu", "itemid", "item",
               "itemno", "malzemeno", "malzemekodu", "malzeme", "pn", "stokkodu", "kod", "code",
               "cizimno", "drawingno", "number", "no"), ("partno", "partnumber", "parcano", "itemid")),
    ("seviye", ("seviye", "level", "lvl", "bomlevel", "bomseviyesi"), ()),
    ("adet", ("adet", "qty", "quantity", "miktar", "count", "kullanimmiktari", "qtyper"), ()),
    ("fastener", (), ("fastener", "baglantielemani", "baglantielemanlari", "hardware", "civata")),
    ("toplam", ("toplam", "total", "totalwt", "toplamwt"), ("toplamagirlik", "totalweight", "totalmass",
                                                            "toplamkutle", "grossweight")),
    ("agirlik", ("wt", "agirlik", "weight", "mass", "kutle"), WEIGHT_WORDS),
    ("isim", ("isim", "ad", "adi", "tanim", "aciklama", "description", "desc", "name", "partname",
              "parcaadi", "parcaismi", "parcaaciklamasi", "title", "nomenclature", "malzemeadi"), ()),
]
NUMERIC_FIELDS = ("agirlik", "fastener", "toplam")


def detect_columns(header: list, overrides: dict[str, str | None]) -> dict[str, int]:
    nh = [norm(h) for h in header]
    cols: dict[str, int] = {}
    used: set[int] = set()
    # 1) kullanıcının verdikleri
    for key, name in overrides.items():
        if name:
            if norm(name) not in nh:
                raise ValueError(f"'{name}' sütunu bulunamadı. Başlıklar: {[h for h in header if h not in (None, '')]}")
            cols[key] = nh.index(norm(name))
            used.add(cols[key])
    # 2) tam eşleşme, 3) içinde geçme
    for pass_ in ("exact", "contains"):
        for key, exact, contains in ALIASES:
            if key in cols:
                continue
            for i, h in enumerate(nh):
                if i in used or not h:
                    continue
                if (pass_ == "exact" and h in exact) or (pass_ == "contains" and any(w in h for w in contains)):
                    cols[key] = i
                    used.add(i)
                    break
    return cols


# ----------------------------------------------------------------------------
# Dosya okuma
# ----------------------------------------------------------------------------

def parse_num(v) -> float | None:
    if v is None or isinstance(v, bool):
        return None
    if isinstance(v, (int, float)):
        return float(v)
    s = str(v).strip().lower().replace("kg", "").replace(" ", "").replace(" ", "")
    if not s or s in ("-", "--", "n/a", "na", "#n/a", "#value!", "#ref!", "#div/0!"):
        return None
    if "," in s and "." in s:
        s = s.replace(".", "").replace(",", ".") if s.rfind(",") > s.rfind(".") else s.replace(",", "")
    elif "," in s:
        s = s.replace(",", ".")
    try:
        return float(s)
    except ValueError:
        return None


def parse_level(v) -> int | None:
    if v is None or str(v).strip() == "":
        return None
    n = parse_num(v)
    if n is not None:
        return int(n)
    s = str(v).strip()
    m = re.search(r"\d+", s)
    return int(m.group()) if m else (s.count(".") or None)


def read_sheets(path: Path) -> dict[str, list[list]]:
    suf = path.suffix.lower()
    if suf in (".xlsx", ".xlsm", ".xltx", ".xltm"):
        from openpyxl import load_workbook
        wb = load_workbook(path, read_only=True, data_only=True)
        out = {ws.title: [list(r) for r in ws.iter_rows(values_only=True)] for ws in wb.worksheets}
        wb.close()
        return out
    if suf == ".xls":
        try:
            import xlrd  # type: ignore
        except ImportError:
            raise SystemExit(f"{path.name}: .xls okumak için 'pip install xlrd' gerekli "
                             "(veya dosyayı Excel'de .xlsx olarak kaydedin).")
        book = xlrd.open_workbook(str(path))
        return {sh.name: [sh.row_values(i) for i in range(sh.nrows)] for sh in book.sheets()}
    raw = path.read_bytes()
    for enc in ("utf-8-sig", "cp1254", "latin-1"):
        try:
            text = raw.decode(enc)
            break
        except UnicodeDecodeError:
            continue
    try:
        dialect = csv.Sniffer().sniff(text[:4096], delimiters=";,\t")
    except csv.Error:
        dialect = csv.excel
    return {path.stem: list(csv.reader(text.splitlines(), dialect))}


@dataclass
class Row:
    key: str
    level: int | None
    values: dict[str, float | None]   # agirlik / fastener / toplam / adet
    texts: dict[str, str]             # diğer sütunlar (başlık -> değer)
    leaf: bool = True


@dataclass
class Bom:
    path: Path
    sheet: str
    header: list[str]
    cols: dict[str, int]
    rows: list[Row] = field(default_factory=list)


def load_bom(path: Path, sheet: str | None, overrides: dict, key_cols: list[str] | None) -> Bom:
    sheets = read_sheets(path)
    if sheet:
        match = [n for n in sheets if norm(n) == norm(sheet)]
        if not match:
            raise SystemExit(f"{path.name}: '{sheet}' sayfası yok. Sayfalar: {list(sheets)}")
        sheets = {match[0]: sheets[match[0]]}

    # Başlık satırını bul: ilk 40 satırda en çok sütun tanınan ve parça sütunu olan satır
    best = None  # (skor, sayfa, satır_index, cols)
    for name, rows in sheets.items():
        for i, r in enumerate(rows[:40]):
            if not r or sum(1 for v in r if v not in (None, "")) < 2:
                continue
            try:
                cols = detect_columns(r, overrides)
            except ValueError:
                continue
            if "parca" not in cols and not key_cols:
                continue
            if not any(k in cols for k in NUMERIC_FIELDS):
                continue
            score = len(cols)
            if best is None or score > best[0]:
                best = (score, name, i, cols)
    if best is None:
        hint = "--parca / --agirlik ile sütun adlarını verin veya --sayfa ile sayfayı seçin."
        raise SystemExit(f"{path.name}: başlık satırı bulunamadı (parça no + ağırlık sütunu). {hint}")
    _, sheet_name, hi, cols = best
    rows = sheets[sheet_name]
    header = [str(h).strip() if h is not None else f"Sütun{j + 1}" for j, h in enumerate(rows[hi])]

    key_idx = [cols["parca"]] if not key_cols else []
    for kc in key_cols or []:
        nh = [norm(h) for h in header]
        if norm(kc) not in nh:
            raise SystemExit(f"{path.name}: anahtar sütunu '{kc}' yok. Başlıklar: {header}")
        key_idx.append(nh.index(norm(kc)))
    special = set(cols.values()) | set(key_idx)

    bom = Bom(path, sheet_name, header, cols)
    for r in rows[hi + 1:]:
        cell = lambda j: r[j] if j < len(r) else None  # noqa: E731
        key = " | ".join(str(cell(j) if cell(j) is not None else "").strip() for j in key_idx)
        if key.endswith(".0") and isinstance(cell(key_idx[0]), float) and len(key_idx) == 1:
            key = key[:-2]
        if not key.strip(" |"):
            continue  # boş / toplam satırları
        if norm(key) in ("toplam", "total", "geneltoplam", "grandtotal", "aratoplam", "subtotal"):
            continue
        values = {k: parse_num(cell(cols[k])) for k in (*NUMERIC_FIELDS, "adet") if k in cols}
        texts = {header[j]: str(cell(j)).strip() for j in range(len(header))
                 if j not in special and cell(j) not in (None, "")}
        if "isim" in cols:
            texts["__isim__"] = str(cell(cols["isim"]) or "").strip()
        level = parse_level(cell(cols["seviye"])) if "seviye" in cols else None
        bom.rows.append(Row(key, level, values, texts))

    # yaprak satırlar (bir sonraki satırın seviyesi daha derin değilse)
    for a, b in zip(bom.rows, bom.rows[1:]):
        if a.level is not None and b.level is not None and b.level > a.level:
            a.leaf = False
    return bom


# ----------------------------------------------------------------------------
# Karşılaştırma
# ----------------------------------------------------------------------------

@dataclass
class Part:
    key: str
    name: str = ""
    n_rows: int = 0
    qty: float = 0.0
    w: float = 0.0
    f: float = 0.0
    t: float = 0.0
    texts: dict[str, set] = field(default_factory=dict)


def row_numbers(row: Row, bom: Bom, multiply_qty: bool) -> tuple[float, float, float, float]:
    v = row.values
    qty = v.get("adet") if v.get("adet") is not None else 1.0
    m = qty if multiply_qty else 1.0
    w = (v.get("agirlik") or 0.0) * m
    f = (v.get("fastener") or 0.0) * m
    if "toplam" in bom.cols and v.get("toplam") is not None:
        t = v["toplam"] * m
    else:
        t = w + f
    return qty, w, f, t


def aggregate(bom: Bom, multiply_qty: bool) -> dict[str, Part]:
    parts: dict[str, Part] = {}
    for r in bom.rows:
        p = parts.setdefault(r.key, Part(r.key))
        qty, w, f, t = row_numbers(r, bom, multiply_qty)
        p.n_rows += 1
        p.qty += qty
        p.w += w
        p.f += f
        p.t += t
        p.w, p.f, p.t = round(p.w, 9), round(p.f, 9), round(p.t, 9)
        if not p.name and r.texts.get("__isim__"):
            p.name = r.texts["__isim__"]
        for k, val in r.texts.items():
            if k != "__isim__":
                p.texts.setdefault(k, set()).add(val)
    return parts


def bom_totals(bom: Bom, method: str, multiply_qty: bool) -> tuple[float, float, float, str]:
    if method == "auto":
        method = "yaprak" if "seviye" in bom.cols else "hepsi"
    rows = bom.rows
    if method == "yaprak":
        rows = [r for r in rows if r.leaf]
    elif method == "kok":
        levels = [r.level for r in rows if r.level is not None]
        lo = min(levels) if levels else None
        rows = [r for r in rows if r.level == lo]
    w = f = t = 0.0
    for r in rows:
        _, a, b, c = row_numbers(r, bom, multiply_qty)
        w, f, t = w + a, f + b, t + c
    return round(w, 9), round(f, 9), round(t, 9), method


ADDED, REMOVED, CHANGED, INFO, SAME = "EKLENDİ", "SİLİNDİ", "DEĞİŞTİ", "BİLGİ DEĞİŞTİ", "AYNI"


@dataclass
class Diff:
    status: str
    key: str
    name: str
    old: Part | None
    new: Part | None
    other: str = ""

    def val(self, attr: str, side: str) -> float | None:
        p = self.old if side == "old" else self.new
        return getattr(p, attr) if p else None

    def delta(self, attr: str) -> float:
        return round((self.val(attr, "new") or 0.0) - (self.val(attr, "old") or 0.0), 9)


def compare(old: dict[str, Part], new: dict[str, Part], tol: float) -> list[Diff]:
    out: list[Diff] = []
    for key in list(old) + [k for k in new if k not in old]:
        o, n = old.get(key), new.get(key)
        name = (n.name if n and n.name else (o.name if o else ""))
        if o is None:
            out.append(Diff(ADDED, key, name, None, n))
            continue
        if n is None:
            out.append(Diff(REMOVED, key, name, o, None))
            continue
        changes = []
        if o.name != n.name and (o.name or n.name):
            changes.append(f"İsim: {o.name} → {n.name}")
        for col in sorted(set(o.texts) | set(n.texts)):
            a, b = o.texts.get(col, set()), n.texts.get(col, set())
            if a != b:
                changes.append(f"{col}: {', '.join(sorted(a)) or '-'} → {', '.join(sorted(b)) or '-'}")
        d = Diff(SAME, key, name, o, n, "; ".join(changes))
        if any(abs(d.delta(a)) > tol for a in ("w", "f", "t")) or abs(d.delta("qty")) > 1e-9 \
                or o.n_rows != n.n_rows:
            d.status = CHANGED
        elif changes:
            d.status = INFO
        out.append(d)
    order = {CHANGED: 0, ADDED: 1, REMOVED: 2, INFO: 3, SAME: 4}
    out.sort(key=lambda d: (order[d.status] if d.status in (INFO, SAME) else 0, -abs(d.delta("t"))))
    return out


# ----------------------------------------------------------------------------
# Rapor
# ----------------------------------------------------------------------------

def pct(old: float | None, new: float | None) -> float | None:
    if not old:
        return None
    return ((new or 0.0) - old) / old


def write_report(path: Path, old_bom: Bom, new_bom: Bom, diffs: list[Diff],
                 totals: tuple, tol: float) -> None:
    from openpyxl import Workbook
    from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
    from openpyxl.utils import get_column_letter

    has_f = "fastener" in old_bom.cols or "fastener" in new_bom.cols
    has_q = "adet" in old_bom.cols or "adet" in new_bom.cols
    has_t = "toplam" in old_bom.cols or "toplam" in new_bom.cols

    FILL = {ADDED: "C6EFCE", REMOVED: "FFC7CE", CHANGED: "FFEB9C", INFO: "DDEBF7", SAME: None}
    head_font = Font(bold=True, color="FFFFFF")
    head_fill = PatternFill("solid", fgColor="305496")
    thin = Side(style="thin", color="BFBFBF")
    border = Border(top=thin, bottom=thin, left=thin, right=thin)
    KG, PCT = "#,##0.000;[Red]-#,##0.000;0.000", "+0.0%;[Red]-0.0%;0.0%"
    DKG = '[Red]+#,##0.000;[Color10]-#,##0.000;0.000'  # artış kırmızı, azalış yeşil

    wb = Workbook()

    # --- Özet ---
    ws = wb.active
    ws.title = "Özet"
    (ow, of_, ot, method), (nw, nf, nt, _) = totals
    ws.append(["Ağırlık BOM Karşılaştırması"])
    ws["A1"].font = Font(bold=True, size=14)
    ws.append(["Eski dosya", str(old_bom.path.name), "Sayfa", old_bom.sheet])
    ws.append(["Yeni dosya", str(new_bom.path.name), "Sayfa", new_bom.sheet])
    ws.append(["Toplam hesabı", {"yaprak": "Yaprak (en alt seviye) satırların toplamı",
                                 "kok": "En üst seviye satırların toplamı",
                                 "hepsi": "Tüm satırların toplamı"}[method]])
    ws.append([])
    ws.append(["", "Eski (kg)", "Yeni (kg)", "Fark (kg)", "Fark %"])
    hr = ws.max_row
    lines = [("Parça ağırlığı", ow, nw)]
    if has_f:
        lines.append(("Fastener ağırlığı", of_, nf))
    lines.append(("TOPLAM ağırlık", ot, nt))
    for label, a, b in lines:
        ws.append([label, a, b, b - a, pct(a, b)])
        r = ws.max_row
        for c in "BC":
            ws[f"{c}{r}"].number_format = KG
        ws[f"D{r}"].number_format = DKG
        ws[f"E{r}"].number_format = PCT
    ws[f"A{ws.max_row}"].font = Font(bold=True)
    for c in range(1, 6):
        ws.cell(hr, c).font, ws.cell(hr, c).fill = head_font, head_fill

    ws.append([])
    ws.append(["Değişiklik sayıları", "Adet", "Toplam ağırlığa etkisi (kg)"])
    hr2 = ws.max_row
    for c in range(1, 4):
        ws.cell(hr2, c).font, ws.cell(hr2, c).fill = head_font, head_fill
    for st in (CHANGED, ADDED, REMOVED, INFO, SAME):
        ds = [d for d in diffs if d.status == st]
        ws.append([st, len(ds), sum(d.delta("t") for d in ds)])
        ws.cell(ws.max_row, 3).number_format = DKG
        if FILL[st]:
            ws.cell(ws.max_row, 1).fill = PatternFill("solid", fgColor=FILL[st])
    explained = sum(d.delta("t") for d in diffs)
    ws.append(["Parça farklarının toplamı", "", explained])
    ws.cell(ws.max_row, 3).number_format = DKG
    if abs(explained - (nt - ot)) > max(tol, 1e-6):
        ws.append(["Not: parça farkları toplamı, BOM toplam farkından farklı — montaj satırlarında "
                   "ağırlık tekrar ediyor olabilir (Toplam hesabı yukarıda yazıyor; --toplam-yontemi ile değiştirilebilir)."])
    ws.append(["Tolerans (kg)", tol])
    ws.column_dimensions["A"].width = 30
    for c in "BCDE":
        ws.column_dimensions[c].width = 18

    # --- Detay sayfaları ---
    headers = ["Durum", "Parça No", "İsim", "Satır sayısı (eski)", "Satır sayısı (yeni)"]
    fields: list[tuple[str, str]] = []
    if has_q:
        fields.append(("qty", "Adet"))
    fields.append(("w", "Ağırlık"))
    if has_f:
        fields.append(("f", "Fastener"))
    fields.append(("t", "Toplam" if has_t else "Toplam (Ağırlık+Fastener)"))
    for attr, lbl in fields:
        headers += [f"{lbl} eski", f"{lbl} yeni", f"{lbl} fark"] + ([f"{lbl} fark %"] if attr != "qty" else [])
    headers.append("Diğer değişiklikler")

    def sheet(title: str, rows: list[Diff]) -> None:
        s = wb.create_sheet(title)
        s.append(headers)
        for c in range(1, len(headers) + 1):
            cell = s.cell(1, c)
            cell.font, cell.fill, cell.border = head_font, head_fill, border
            cell.alignment = Alignment(wrap_text=True, vertical="center", horizontal="center")
        for d in rows:
            line = [d.status, d.key, d.name, d.old.n_rows if d.old else None, d.new.n_rows if d.new else None]
            for attr, _ in fields:
                a, b = d.val(attr, "old"), d.val(attr, "new")
                line += [a, b, d.delta(attr)] + ([pct(a, b)] if attr != "qty" else [])
            line.append(d.other)
            s.append(line)
            r = s.max_row
            fill = FILL[d.status]
            col = 6
            for attr, _ in fields:
                n = 3 if attr == "qty" else 4
                fmt = "0.###" if attr == "qty" else KG
                s.cell(r, col).number_format = s.cell(r, col + 1).number_format = fmt
                s.cell(r, col + 2).number_format = "+0.###;-0.###;0" if attr == "qty" else DKG
                if n == 4:
                    s.cell(r, col + 3).number_format = PCT
                col += n
            if fill:
                s.cell(r, 1).fill = PatternFill("solid", fgColor=fill)
        s.freeze_panes = "D2"
        s.auto_filter.ref = f"A1:{get_column_letter(len(headers))}{max(s.max_row, 2)}"
        s.row_dimensions[1].height = 32
        widths = {1: 15, 2: 20, 3: 36, len(headers): 60}
        for c in range(1, len(headers) + 1):
            s.column_dimensions[get_column_letter(c)].width = widths.get(c, 12)

    sheet("Farklar", [d for d in diffs if d.status in (CHANGED, ADDED, REMOVED)])
    sheet("Bilgi Değişiklikleri", [d for d in diffs if d.status == INFO])
    sheet("Tüm Parçalar", sorted(diffs, key=lambda d: d.key))
    wb.save(path)


# ----------------------------------------------------------------------------
# CLI
# ----------------------------------------------------------------------------

def fmt(x: float) -> str:
    return f"{x:+,.3f}".replace(",", " ")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="İki ağırlık BOM Excel dosyasını karşılaştırır.")
    ap.add_argument("eski", nargs="?", help="eski BOM (.xlsx/.xlsm/.xls/.csv)")
    ap.add_argument("yeni", nargs="?", help="yeni BOM")
    ap.add_argument("-o", "--cikti", help="rapor yolu (varsayılan: agirlik_fark_<eski>_<yeni>.xlsx)")
    ap.add_argument("--sayfa", help="okunacak sayfa adı (varsayılan: otomatik bulunur)")
    ap.add_argument("--parca", help="parça no sütun başlığı")
    ap.add_argument("--isim", help="isim/tanım sütun başlığı")
    ap.add_argument("--agirlik", help="parça ağırlığı sütun başlığı")
    ap.add_argument("--fastener", help="fastener ağırlığı sütun başlığı")
    ap.add_argument("--toplam", help="toplam ağırlık sütun başlığı (yoksa ağırlık+fastener)")
    ap.add_argument("--adet", help="adet sütun başlığı")
    ap.add_argument("--seviye", help="seviye sütun başlığı")
    ap.add_argument("--anahtar", nargs="+", metavar="SÜTUN",
                    help="eşleştirme için kullanılacak sütun(lar), ör. --anahtar \"Parent\" \"Part No\"")
    ap.add_argument("--adet-carp", action="store_true",
                    help="ağırlıklar birim ağırlıksa adet ile çarp (varsayılan: satırdaki değer olduğu gibi)")
    ap.add_argument("--toplam-yontemi", choices=("auto", "hepsi", "yaprak", "kok"), default="auto",
                    help="BOM toplamı: hepsi=tüm satırlar, yaprak=en alt seviye, kok=en üst seviye "
                         "(auto: seviye sütunu varsa yaprak, yoksa hepsi)")
    ap.add_argument("--tolerans", type=float, default=0.001, help="bu kadar (kg) ve altı fark yok sayılır")
    ap.add_argument("--ilk", type=int, default=15, help="ekranda gösterilecek en büyük fark sayısı")
    a = ap.parse_args(argv)

    def ask(prompt: str) -> str:
        return input(prompt).strip().strip('"').strip("'").strip()

    eski = Path(a.eski or ask("Eski BOM dosyası: "))
    yeni = Path(a.yeni or ask("Yeni BOM dosyası: "))
    for p in (eski, yeni):
        if not p.is_file():
            raise SystemExit(f"Dosya bulunamadı: {p}")

    overrides = {"parca": a.parca, "isim": a.isim, "agirlik": a.agirlik, "fastener": a.fastener,
                 "toplam": a.toplam, "adet": a.adet, "seviye": a.seviye}
    ob = load_bom(eski, a.sayfa, overrides, a.anahtar)
    nb = load_bom(yeni, a.sayfa, overrides, a.anahtar)

    for b in (ob, nb):
        found = ", ".join(f"{k}='{b.header[i]}'" for k, i in sorted(b.cols.items(), key=lambda x: x[1]))
        print(f"{b.path.name} [{b.sheet}]: {len(b.rows)} satır | {found}")
    if sorted(ob.cols) != sorted(nb.cols):
        print("UYARI: iki dosyada tanınan sütunlar farklı!", file=sys.stderr)

    diffs = compare(aggregate(ob, a.adet_carp), aggregate(nb, a.adet_carp), a.tolerans)
    totals = (bom_totals(ob, a.toplam_yontemi, a.adet_carp), bom_totals(nb, a.toplam_yontemi, a.adet_carp))
    (ow, of_, ot, method), (nw, nf, nt, _) = totals

    print(f"\nToplam hesabı: {method}")
    print(f"{'':20}{'Eski':>14}{'Yeni':>14}{'Fark':>14}")
    rows = [("Parça ağırlığı", ow, nw)]
    if "fastener" in ob.cols:
        rows.append(("Fastener ağırlığı", of_, nf))
    rows.append(("TOPLAM", ot, nt))
    for label, x, y in rows:
        p = pct(x, y)
        print(f"{label:20}{x:14.3f}{y:14.3f}{fmt(y - x):>14}" + (f"  ({p:+.2%})" if p is not None else ""))

    print()
    for st in (CHANGED, ADDED, REMOVED, INFO):
        print(f"  {st:15}: {sum(1 for d in diffs if d.status == st)}")

    top = [d for d in diffs if d.status in (CHANGED, ADDED, REMOVED)][: a.ilk]
    if top:
        print(f"\nToplam ağırlığa en çok etki eden {len(top)} parça:")
        for d in top:
            extra = f"  (fastener {fmt(d.delta('f'))})" if "fastener" in ob.cols and abs(d.delta("f")) > a.tolerans else ""
            print(f"  {fmt(d.delta('t')):>12} kg  {d.status:9} {d.key[:24]:24} {d.name[:40]}{extra}")

    out = Path(a.cikti or f"agirlik_fark_{eski.stem}_{yeni.stem}.xlsx")
    write_report(out, ob, nb, diffs, totals, a.tolerans)
    print(f"\nRapor: {out.resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
