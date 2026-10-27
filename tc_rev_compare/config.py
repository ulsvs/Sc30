"""Ayar dosyası (TOML) okuma ve varsayılanlar."""

from __future__ import annotations

import re
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

from .compare import Tolerances
from .model import BomNode

DEFAULT_FILE_COLUMNS = {
    "seviye": ["Seviye", "Level", "bl_level_starting_0", "BOM Level"],
    "item_id": ["Item ID", "Item Id", "ItemID", "Parça No", "Parca No", "bl_item_item_id"],
    "revizyon": ["Revizyon", "Rev", "Revision", "Rev ID", "bl_rev_item_revision_id"],
    "isim": ["İsim", "Isim", "Ad", "Name", "Object Name", "bl_rev_object_name"],
    "find_no": ["Find No", "Find No.", "Sıra No", "bl_sequence_no"],
    "agirlik": ["Ağırlık", "Agirlik", "Kütle", "Weight", "Mass"],
    "adet": ["Adet", "Paket Adedi", "Pack Count", "bl_pack_count"],
    "x": ["X", "X (mm)"],
    "y": ["Y", "Y (mm)"],
    "z": ["Z", "Z (mm)"],
    "donusum": ["Dönüşüm", "Transform", "bl_plmxml_occ_xform"],
}


@dataclass
class Config:
    tc_url: str = "http://tcserver:7001/tc"
    tc_user: str = ""
    tc_group: str = ""
    tc_role: str = ""
    tc_verify_ssl: bool = True
    tc_timeout: int = 600
    default_item_id: str = ""
    rev_rule: str = ""
    old_rev_rule: str = ""
    target: str = "son"

    prop_item_id: str = "bl_item_item_id"
    prop_revision: str = "bl_rev_item_revision_id"
    prop_name: str = "bl_rev_object_name"
    prop_find_no: str = "bl_sequence_no"
    prop_pack_count: str = "bl_pack_count"
    prop_transform: str = "bl_plmxml_occ_xform"
    prop_abs_transform: str = "bl_plmxml_abs_xform"
    prop_weight: list[str] = field(default_factory=lambda: ["bl_rev_weight", "bl_rev_mass"])

    weight_factor: float = 1.0
    position_factor: float = 1000.0
    tolerances: Tolerances = field(default_factory=Tolerances)
    file_columns: dict[str, list[str]] = field(default_factory=lambda: dict(DEFAULT_FILE_COLUMNS))

    @classmethod
    def load(cls, path: str | Path | None) -> "Config":
        cfg = cls()
        if path is None or not Path(path).exists():
            return cfg
        with open(path, "rb") as f:
            data = tomllib.load(f)
        tc = data.get("teamcenter", {})
        cfg.tc_url = tc.get("url", cfg.tc_url)
        cfg.tc_user = tc.get("kullanici", cfg.tc_user)
        cfg.tc_group = tc.get("grup", cfg.tc_group)
        cfg.tc_role = tc.get("rol", cfg.tc_role)
        cfg.tc_verify_ssl = tc.get("ssl_dogrula", cfg.tc_verify_ssl)
        cfg.tc_timeout = int(tc.get("zaman_asimi_sn", cfg.tc_timeout))
        cfg.default_item_id = tc.get("varsayilan_item_id", cfg.default_item_id)
        cfg.rev_rule = tc.get("revizyon_kurali", cfg.rev_rule)
        cfg.old_rev_rule = tc.get("eski_revizyon_kurali", cfg.old_rev_rule)
        cfg.target = tc.get("hedef", cfg.target)

        p = data.get("ozellikler", {})
        cfg.prop_item_id = p.get("item_id", cfg.prop_item_id)
        cfg.prop_revision = p.get("revizyon", cfg.prop_revision)
        cfg.prop_name = p.get("isim", cfg.prop_name)
        cfg.prop_find_no = p.get("find_no", cfg.prop_find_no)
        cfg.prop_pack_count = p.get("paket_adedi", cfg.prop_pack_count)
        cfg.prop_transform = p.get("lokal_donusum", cfg.prop_transform)
        cfg.prop_abs_transform = p.get("mutlak_donusum", cfg.prop_abs_transform)
        w = p.get("agirlik", cfg.prop_weight)
        cfg.prop_weight = [w] if isinstance(w, str) else list(w)

        u = data.get("birimler", {})
        cfg.weight_factor = float(u.get("agirlik_carpani", cfg.weight_factor))
        cfg.position_factor = float(u.get("konum_carpani", cfg.position_factor))

        t = data.get("toleranslar", {})
        cfg.tolerances = Tolerances(
            weight_kg=float(t.get("agirlik_kg", cfg.tolerances.weight_kg)),
            weight_pct=float(t.get("agirlik_yuzde", cfg.tolerances.weight_pct)),
            position_mm=float(t.get("konum_mm", cfg.tolerances.position_mm)),
            rotation_deg=float(t.get("aci_derece", cfg.tolerances.rotation_deg)),
            replace_mm=float(t.get("degistirme_mm", cfg.tolerances.replace_mm)),
        )
        for key, val in data.get("dosya_sutunlari", {}).items():
            cfg.file_columns[key] = [val] if isinstance(val, str) else list(val)
        return cfg

    # ---- Teamcenter BOM satırı -> BomNode ------------------------------
    def bomline_properties(self) -> list[str]:
        names = [self.prop_item_id, self.prop_revision, self.prop_name, self.prop_find_no,
                 self.prop_pack_count, self.prop_transform, self.prop_abs_transform, *self.prop_weight]
        return [n for n in dict.fromkeys(names) if n]

    def node_from_props(self, props: dict[str, str]) -> BomNode:
        weight = None
        for name in self.prop_weight:
            weight = parse_float(props.get(name, ""))
            if weight is not None:
                weight *= self.weight_factor
                break
        count = parse_float(props.get(self.prop_pack_count, "")) or 1.0
        return BomNode(
            item_id=props.get(self.prop_item_id, ""),
            revision=props.get(self.prop_revision, ""),
            name=props.get(self.prop_name, ""),
            find_no=props.get(self.prop_find_no, ""),
            weight=weight,
            count=max(count, 1.0),
            transform=self.parse_transform(props.get(self.prop_transform, "")),
            abs_transform=self.parse_transform(props.get(self.prop_abs_transform, "")),
        )

    def parse_transform(self, text: str) -> list[float] | None:
        values = [parse_float(v) for v in str(text).replace(",", " ").split()]
        if len(values) != 16 or any(v is None for v in values):
            return None
        m = [float(v) for v in values]  # type: ignore[arg-type]
        for i in (12, 13, 14):
            m[i] *= self.position_factor
        return m


def parse_float(text) -> float | None:
    """'12.5', '12,5', '1.234,5', '1,234.5', '3.2 kg' gibi değerleri okur."""
    if text is None:
        return None
    if isinstance(text, (int, float)):
        return float(text)
    s = re.sub(r"\s*[A-Za-z]+$", "", str(text).strip())  # "3.2 kg" -> "3.2"
    if not s:
        return None
    if "," in s and "." in s:
        if s.rfind(",") > s.rfind("."):
            s = s.replace(".", "").replace(",", ".")
        else:
            s = s.replace(",", "")
    elif "," in s:
        s = s.replace(",", ".")
    try:
        return float(s)
    except ValueError:
        return None
