"""Komut satırı.

    python -m tc_rev_compare 123456/A                 # A ile en güncel revizyonu karşılaştır
    python -m tc_rev_compare A                        # ayarlardaki varsayilan_item_id ile
    python -m tc_rev_compare 123456/A --yeni-rev F    # belirli bir revizyonla
    python -m tc_rev_compare --dosya eski.xlsx yeni.xlsx
    python -m tc_rev_compare 123456/A --ozellik-test bl_rev_weight bl_rev_mass
"""

from __future__ import annotations

import argparse
import getpass
import logging
import os
import sys
from datetime import datetime
from pathlib import Path

from .compare import compare
from .config import Config
from .fileloader import load_bom_file
from .report import summary_text, write_excel

log = logging.getLogger("tc_rev_compare")


def _find_config(explicit: str | None) -> Path | None:
    if explicit:
        return Path(explicit)
    for p in (Path.cwd() / "ayarlar.toml", Path(__file__).resolve().parent.parent / "ayarlar.toml"):
        if p.exists():
            return p
    return None


def _safe(s: str) -> str:
    return "".join(ch if ch.isalnum() or ch in "-_." else "_" for ch in s)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        prog="tc_rev_compare",
        description="Teamcenter'da eski bir revizyonu en güncel revizyonla karşılaştırır "
                    "(ağırlık, konum, isimlendirme).")
    ap.add_argument("revizyon", nargs="?", help="ITEM_ID/REV (ör. 123456/A) veya sadece REV")
    ap.add_argument("--yeni-rev", help="karşılaştırılacak yeni revizyon (varsayılan: en güncel)")
    ap.add_argument("--hedef", choices=["son", "yayinlanmis"],
                    help="'son' = en son oluşturulan, 'yayinlanmis' = en son released revizyon")
    ap.add_argument("--eski-tarih", help="eski BOM için revizyon kuralı tarihi (YYYY-AA-GG)")
    ap.add_argument("--dosya", nargs=2, metavar=("ESKI", "YENI"),
                    help="Teamcenter yerine CSV/XLSX BOM export'larını karşılaştır")
    ap.add_argument("--ozellik-test", nargs="+", metavar="OZELLIK",
                    help="verilen BOM satırı özelliklerinin değerlerini gösterir (ağırlık özelliğini bulmak için)")
    ap.add_argument("--ayarlar", help="ayar dosyası (varsayılan: ayarlar.toml)")
    ap.add_argument("--cikti", help="Excel rapor yolu (varsayılan: otomatik isim)")
    ap.add_argument("--ilk", type=int, default=10, help="özette gösterilecek satır sayısı")
    ap.add_argument("-v", "--ayrintili", action="store_true")
    args = ap.parse_args(argv)

    logging.basicConfig(level=logging.DEBUG if args.ayrintili else logging.INFO, format="%(message)s")
    cfg = Config.load(_find_config(args.ayarlar))

    if args.dosya:
        old_root = load_bom_file(args.dosya[0], cfg)
        new_root = load_bom_file(args.dosya[1], cfg)
        header = f"Eski: {args.dosya[0]}  ({old_root.label})\nYeni: {args.dosya[1]}  ({new_root.label})"
        default_out = f"karsilastirma_{_safe(old_root.label)}_{_safe(new_root.label)}.xlsx"
        return _finish(old_root, new_root, cfg, header, args.cikti or default_out, args.ilk)

    if not args.revizyon:
        ap.error("eski revizyonu girin (ör. 123456/A) ya da --dosya kullanın")
    if "/" in args.revizyon:
        item_id, old_rev_id = args.revizyon.rsplit("/", 1)
    elif cfg.default_item_id:
        item_id, old_rev_id = cfg.default_item_id, args.revizyon
    else:
        ap.error("Item ID belirtilmedi: '123456/A' şeklinde girin veya ayarlarda varsayilan_item_id tanımlayın")

    return _run_teamcenter(cfg, item_id.strip(), old_rev_id.strip(), args)


def _run_teamcenter(cfg: Config, item_id: str, old_rev_id: str, args) -> int:
    from .teamcenter import TeamcenterClient, TeamcenterError, latest_revision

    user = cfg.tc_user or os.environ.get("TC_USER") or input("Teamcenter kullanıcı adı: ")
    password = os.environ.get("TC_PASSWORD") or getpass.getpass(f"{user} şifresi: ")
    tc = TeamcenterClient(cfg)
    try:
        log.info("Teamcenter'a bağlanılıyor: %s", cfg.tc_url)
        tc.login(user, password, cfg.tc_group, cfg.tc_role)
        item = tc.find_item(item_id)
        revs = tc.revisions(item)
        by_id = {r.rev_id: r for r in revs}
        if old_rev_id not in by_id:
            raise TeamcenterError(f"{item_id} için '{old_rev_id}' revizyonu yok. Mevcut: {', '.join(by_id)}")
        old = by_id[old_rev_id]
        if args.yeni_rev:
            if args.yeni_rev not in by_id:
                raise TeamcenterError(f"'{args.yeni_rev}' revizyonu yok. Mevcut: {', '.join(by_id)}")
            new = by_id[args.yeni_rev]
        else:
            new = latest_revision(revs, (args.hedef or cfg.target) == "yayinlanmis")
        log.info("Revizyonlar: %s  ->  en güncel: %s", ", ".join(by_id), new.rev_id)
        if new.rev_id == old.rev_id:
            log.warning("Eski ve yeni revizyon aynı (%s); karşılaştırılacak bir şey yok.", old.rev_id)
            return 0

        rule_new = tc.revision_rule(cfg.rev_rule)
        rule_old = tc.revision_rule(cfg.old_rev_rule) if cfg.old_rev_rule else rule_new
        old_date = datetime.strptime(args.eski_tarih, "%Y-%m-%d").replace(hour=23, minute=59) \
            if args.eski_tarih else None

        if args.ozellik_test:
            root = tc.load_bom(item, new.ref, rule_new, extra_props=args.ozellik_test)
            _print_probe(root, args.ozellik_test, args.ilk)
            return 0

        log.info("Eski BOM açılıyor: %s/%s", item_id, old.rev_id)
        old_root = tc.load_bom(item, old.ref, rule_old, old_date)
        log.info("Yeni BOM açılıyor: %s/%s", item_id, new.rev_id)
        new_root = tc.load_bom(item, new.ref, rule_new)
    except TeamcenterError as e:
        log.error("HATA: %s", e)
        return 2
    finally:
        tc.logout()

    header = (f"Eski: {item_id}/{old.rev_id}  {old.name}  (oluşturma {old.created[:10]}, yayın {old.released[:10] or '-'})\n"
              f"Yeni: {item_id}/{new.rev_id}  {new.name}  (oluşturma {new.created[:10]}, yayın {new.released[:10] or '-'})")
    out = args.cikti or f"karsilastirma_{_safe(item_id)}_{_safe(old.rev_id)}_{_safe(new.rev_id)}.xlsx"
    return _finish(old_root, new_root, cfg, header, out, args.ilk)


def _print_probe(root, names: list[str], limit: int) -> None:
    nodes = list(root.walk())
    print(f"{len(nodes)} satır okundu. Her özellik için dolu satır sayısı:")
    for n in names:
        filled = [x for x in nodes if x.extra.get(n)]
        print(f"  {n:<35} {len(filled):>6} dolu" + (f"   ör. {filled[0].label}: {filled[0].extra[n]}" if filled else ""))
    print(f"\nİlk {limit} satır:")
    for x in nodes[:limit]:
        vals = "  ".join(f"{n}={x.extra.get(n, '')!r}" for n in names)
        print(f"  {'  ' * x.level}{x.label:<20} {vals}")


def _finish(old_root, new_root, cfg: Config, header: str, out: str, top: int) -> int:
    result = compare(old_root, new_root, cfg.tolerances)
    print()
    print(summary_text(result, header, top))
    path = write_excel(result, out, header)
    print(f"\nAyrıntılı rapor: {path.resolve()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
