"""İki BOM ağacını eşleştirip ağırlık / konum / isim farklarını çıkarır.

Eşleştirme ağaç üzerinde yukarıdan aşağı yapılır: eşleşen iki ebeveynin
çocukları önce Item ID'ye göre gruplanır, aynı Item ID'nin birden fazla
kopyası varsa Find No ve en yakın lokal konuma göre eşleştirilir. Eşleşmeyen
satırlar aynı Find No veya aynı konumdaysa "değiştirildi" (replace), değilse
"eklendi" / "silindi" sayılır.
"""

from __future__ import annotations

import math
from collections import defaultdict
from dataclasses import dataclass, field

from .model import BomNode, distance, rotation_angle_deg

ESLESTI = "eşleşti"
EKLENDI = "eklendi"
SILINDI = "silindi"
DEGISTIRILDI = "değiştirildi"


@dataclass
class Tolerances:
    weight_kg: float = 0.001
    weight_pct: float = 0.0
    position_mm: float = 0.1
    rotation_deg: float = 0.01
    replace_mm: float = 1.0


@dataclass
class LineDiff:
    status: str
    old: BomNode | None
    new: BomNode | None
    weight_delta: float = 0.0       # toplam ağırlığa etkisi (kg)
    counted: bool = True            # weight_delta toplama dahil mi
    name_changed: bool = False
    rev_changed: bool = False
    weight_changed: bool = False
    moved: bool = False
    rotated: bool = False
    distance_mm: float = 0.0
    rotation_deg: float = 0.0
    note: str = ""

    @property
    def node(self) -> BomNode:
        return self.new or self.old  # type: ignore[return-value]

    @property
    def has_change(self) -> bool:
        return (self.status != ESLESTI or self.name_changed or self.weight_changed
                or self.moved or self.rotated)


@dataclass
class CompareResult:
    old_root: BomNode
    new_root: BomNode
    diffs: list[LineDiff] = field(default_factory=list)

    @property
    def old_total(self) -> float | None:
        return self.old_root.rolled_weight()

    @property
    def new_total(self) -> float | None:
        return self.new_root.rolled_weight()

    def of(self, status: str) -> list[LineDiff]:
        return [d for d in self.diffs if d.status == status]

    @property
    def weight_changes(self) -> list[LineDiff]:
        return [d for d in self.diffs if d.status == ESLESTI and d.weight_changed]

    @property
    def position_changes(self) -> list[LineDiff]:
        return [d for d in self.diffs if d.status == ESLESTI and (d.moved or d.rotated)]

    @property
    def name_changes(self) -> list[LineDiff]:
        return [d for d in self.diffs if d.status == ESLESTI and d.name_changed]

    @property
    def changed(self) -> list[LineDiff]:
        return [d for d in self.diffs if d.has_change]

    def top_weight_impacts(self, n: int = 10) -> list[LineDiff]:
        rows = [d for d in self.diffs if d.counted and abs(d.weight_delta) > 0]
        return sorted(rows, key=lambda d: abs(d.weight_delta), reverse=True)[:n]

    @property
    def explained_delta(self) -> float:
        return sum(d.weight_delta for d in self.diffs if d.counted)


def compare(old_root: BomNode, new_root: BomNode, tol: Tolerances | None = None) -> CompareResult:
    tol = tol or Tolerances()
    result = CompareResult(old_root, new_root)
    _compare_pair(old_root, new_root, tol, result.diffs, mult=1.0)
    return result


def _weight(node: BomNode) -> float:
    return node.rolled_weight() or 0.0


def _weight_differs(a: float | None, b: float | None, tol: Tolerances) -> bool:
    if a is None and b is None:
        return False
    if a is None or b is None:
        return True
    diff = abs(a - b)
    if diff <= tol.weight_kg:
        return False
    if tol.weight_pct and a and diff / abs(a) * 100 <= tol.weight_pct:
        return False
    return True


def _compare_pair(old: BomNode, new: BomNode, tol: Tolerances, out: list[LineDiff], mult: float) -> None:
    d = LineDiff(ESLESTI, old, new)
    d.name_changed = old.name.strip() != new.name.strip()
    d.rev_changed = old.revision != new.revision
    d.weight_changed = _weight_differs(old.weight, new.weight, tol)
    if old.parent is not None:  # kök satırın konumu karşılaştırılmaz
        d.distance_mm = distance(old.local_position, new.local_position)
        if math.isinf(d.distance_mm):
            d.distance_mm = 0.0
        d.moved = d.distance_mm > tol.position_mm
        d.rotation_deg = rotation_angle_deg(old.transform, new.transform)
        d.rotated = d.rotation_deg > tol.rotation_deg

    both_assemblies = bool(old.children) and bool(new.children)
    if both_assemblies:
        # Ağırlık etkisi çocuklarda hesaplanır; burada sadece bilgi amaçlı.
        d.counted = False
        d.weight_delta = (_weight(new) - _weight(old)) * mult
    else:
        d.weight_delta = (_weight(new) - _weight(old)) * mult
        if old.children or new.children:
            d.note = "Parça ↔ montaj yapısı değişti; alt seviye ayrıca listelenmedi"
            d.weight_changed = d.weight_changed or _weight_differs(old.rolled_weight(), new.rolled_weight(), tol)
    out.append(d)

    if both_assemblies:
        _compare_children(old, new, tol, out, mult * new.count)


def _pair_cost(o: BomNode, n: BomNode) -> tuple:
    same_find = bool(o.find_no) and o.find_no == n.find_no
    return (0 if same_find else 1, distance(o.local_position, n.local_position))


def _greedy_pairs(olds: list[BomNode], news: list[BomNode]) -> list[tuple[BomNode, BomNode]]:
    if len(olds) == 1 and len(news) == 1:
        return [(olds[0], news[0])]
    candidates = []
    for i, o in enumerate(olds):
        for j, n in enumerate(news):
            candidates.append((_pair_cost(o, n), abs(i - j), i, j))
    candidates.sort()
    used_o, used_n, pairs = set(), set(), []
    for _, _, i, j in candidates:
        if i in used_o or j in used_n:
            continue
        used_o.add(i)
        used_n.add(j)
        pairs.append((olds[i], news[j]))
    return pairs


def _compare_children(old: BomNode, new: BomNode, tol: Tolerances, out: list[LineDiff], mult: float) -> None:
    old_groups: dict[str, list[BomNode]] = defaultdict(list)
    new_groups: dict[str, list[BomNode]] = defaultdict(list)
    for c in old.children:
        old_groups[c.item_id].append(c)
    for c in new.children:
        new_groups[c.item_id].append(c)

    matched: list[tuple[BomNode, BomNode]] = []
    for item_id in list(dict.fromkeys([*old_groups, *new_groups])):
        matched.extend(_greedy_pairs(old_groups.get(item_id, []), new_groups.get(item_id, [])))

    paired_old = {id(o) for o, _ in matched}
    paired_new = {id(n) for _, n in matched}
    rest_old = [c for c in old.children if id(c) not in paired_old]
    rest_new = [c for c in new.children if id(c) not in paired_new]

    # Farklı Item ID ama aynı Find No / aynı konum -> parça değiştirilmiş
    replaced: list[tuple[BomNode, BomNode]] = []
    for (same_find, dist), _, i, j in sorted(
            (_pair_cost(o, n), abs(i - j), i, j)
            for i, o in enumerate(rest_old) for j, n in enumerate(rest_new)):
        if same_find == 1 and dist > tol.replace_mm:
            continue
        o, n = rest_old[i], rest_new[j]
        if any(o is a or n is b for a, b in replaced):
            continue
        replaced.append((o, n))

    for o, n in matched:
        _compare_pair(o, n, tol, out, mult)

    for o, n in replaced:
        d = LineDiff(DEGISTIRILDI, o, n, weight_delta=(_weight(n) - _weight(o)) * mult)
        d.name_changed = o.name.strip() != n.name.strip()
        d.weight_changed = _weight_differs(o.rolled_weight(), n.rolled_weight(), tol)
        d.distance_mm = distance(o.local_position, n.local_position)
        if math.isinf(d.distance_mm):
            d.distance_mm = 0.0
        d.moved = d.distance_mm > tol.position_mm
        d.note = f"{o.item_id} yerine {n.item_id}"
        out.append(d)

    replaced_ids = {id(x) for pair in replaced for x in pair}
    for o in rest_old:
        if id(o) not in replaced_ids:
            out.append(LineDiff(SILINDI, o, None, weight_delta=-_weight(o) * mult))
    for n in rest_new:
        if id(n) not in replaced_ids:
            out.append(LineDiff(EKLENDI, None, n, weight_delta=_weight(n) * mult))
