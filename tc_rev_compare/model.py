"""BOM ağacı veri modeli ve basit 4x4 matris yardımcıları.

Dönüşüm matrisleri PLMXML düzeninde tutulur: satır-öncelikli 16 değer,
öteleme (translation) 12, 13, 14. indekslerde ve **mm** cinsinden.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Iterator

IDENTITY = [1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0]


def mat_mul(a: list[float], b: list[float]) -> list[float]:
    """4x4 satır-öncelikli çarpım (a * b)."""
    return [sum(a[r * 4 + k] * b[k * 4 + c] for k in range(4))
            for r in range(4) for c in range(4)]


def translation(m: list[float] | None) -> tuple[float, float, float] | None:
    if m is None:
        return None
    return (m[12], m[13], m[14])


def rotation_angle_deg(a: list[float] | None, b: list[float] | None) -> float:
    """İki dönüşümün dönme kısımları arasındaki açı (derece)."""
    if a is None or b is None:
        return 0.0
    # Satır vektörü düzeninde R_rel = A^T * B; iz(R_rel) = sum_ij A_ij * B_ij
    trace = sum(a[r * 4 + c] * b[r * 4 + c] for r in range(3) for c in range(3))
    cos = max(-1.0, min(1.0, (trace - 1.0) / 2.0))
    return math.degrees(math.acos(cos))


def distance(p: tuple | None, q: tuple | None) -> float:
    if p is None or q is None:
        return math.inf
    return math.dist(p, q)


@dataclass(eq=False)
class BomNode:
    item_id: str
    revision: str = ""
    name: str = ""
    find_no: str = ""
    weight: float | None = None          # kg, satırın kendi ağırlık özelliği
    count: float = 1.0                    # paketlenmiş satır sayısı vb.
    transform: list[float] | None = None  # ebeveyne göre (lokal), mm
    abs_transform: list[float] | None = None  # montaja göre (mutlak), mm
    extra: dict[str, str] = field(default_factory=dict)  # ham ek özellikler
    children: list["BomNode"] = field(default_factory=list)
    parent: "BomNode | None" = field(default=None, repr=False)

    def add(self, child: "BomNode") -> "BomNode":
        child.parent = self
        self.children.append(child)
        return child

    @property
    def label(self) -> str:
        return f"{self.item_id}/{self.revision}" if self.revision else self.item_id

    @property
    def path(self) -> str:
        parts = []
        node: BomNode | None = self
        while node is not None:
            parts.append(node.item_id + (f"[{node.find_no}]" if node.find_no and node.parent else ""))
            node = node.parent
        return " > ".join(reversed(parts))

    @property
    def level(self) -> int:
        lvl, node = 0, self.parent
        while node is not None:
            lvl, node = lvl + 1, node.parent
        return lvl

    @property
    def local_position(self) -> tuple[float, float, float] | None:
        return translation(self.transform)

    def world_transform(self) -> list[float] | None:
        if self.abs_transform is not None:
            return self.abs_transform
        if self.parent is None:
            return self.transform or IDENTITY
        if self.transform is None:
            return None
        parent_world = self.parent.world_transform()
        if parent_world is None:
            return None
        # Satır vektörü düzeni: dünya = lokal * ebeveyn_dünya
        return mat_mul(self.transform, parent_world)

    @property
    def world_position(self) -> tuple[float, float, float] | None:
        return translation(self.world_transform())

    def walk(self) -> Iterator["BomNode"]:
        yield self
        for c in self.children:
            yield from c.walk()

    def rolled_weight(self) -> float | None:
        """Alt montajlar dahil toplam ağırlık (kg).

        Çocukların hiçbirinde ağırlık yoksa satırın kendi ağırlığı kullanılır.
        """
        if self.children:
            child_weights = [c.rolled_weight() for c in self.children]
            if any(w is not None for w in child_weights):
                return self.count * sum(w or 0.0 for w in child_weights)
        if self.weight is None:
            return None
        return self.count * self.weight
