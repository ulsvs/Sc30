"""Teamcenter SOA JSON-REST istemcisi (web tier: http://sunucu:port/tc).

Kullanılan servisler:
  Core-2011-06-Session/login, Core-2006-03-Session/logout
  Core-2007-01-DataManagement/getItemFromId
  Core-2006-03-DataManagement/getProperties
  Cad-2007-01-StructureManagement/getRevisionRules, createBOMWindows, closeBOMWindows
  Cad-2008-06-StructureManagement/expandPSAllLevels
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from datetime import datetime

import requests

from .config import Config
from .model import BomNode

log = logging.getLogger(__name__)

NULL_UID = "AAAAAAAAAAAAAA"
NULL_REF = {"uid": NULL_UID, "className": "unknownClass", "type": "unknownType"}


class TeamcenterError(RuntimeError):
    pass


@dataclass
class Revision:
    ref: dict
    rev_id: str
    name: str
    created: str
    released: str
    statuses: list[str]


def _is_null(ref: dict | None) -> bool:
    return not ref or ref.get("uid") in (None, "", NULL_UID)


def _service_errors(data: dict) -> list[str]:
    msgs = []
    sd = data.get("ServiceData") or {}
    for pe in sd.get("partialErrors") or []:
        for ev in pe.get("errorValues") or []:
            msgs.append(ev.get("message", str(ev)))
    return msgs


class TeamcenterClient:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.base = cfg.tc_url.rstrip("/") + "/JsonRestServices/"
        self.http = requests.Session()
        self.http.verify = cfg.tc_verify_ssl
        self._objects: dict[str, dict] = {}

    # ---- düşük seviye ---------------------------------------------------
    def call(self, service: str, body: dict) -> dict:
        payload = {"header": {"state": {}, "policy": {}}, "body": body}
        log.debug("TC çağrısı: %s", service)
        try:
            r = self.http.post(self.base + service, json=payload, timeout=self.cfg.tc_timeout)
        except requests.RequestException as e:
            raise TeamcenterError(f"{service}: bağlantı hatası: {e}") from e
        if r.status_code != 200:
            raise TeamcenterError(f"{service}: HTTP {r.status_code}: {r.text[:500]}")
        try:
            data = r.json()
        except ValueError as e:
            raise TeamcenterError(f"{service}: JSON olmayan cevap: {r.text[:500]}") from e
        if isinstance(data, dict) and "Exception" in str(data.get(".QName", "")):
            raise TeamcenterError(f"{service}: {data.get('message') or data}")
        if isinstance(data, dict):
            for uid, mo in ((data.get("ServiceData") or {}).get("modelObjects") or {}).items():
                self._objects[uid] = self._ref(mo)
        return data

    @staticmethod
    def _ref(obj: dict) -> dict:
        return {"uid": obj["uid"], "className": obj.get("className", ""), "type": obj.get("type", "")}

    def ref(self, uid_or_obj) -> dict:
        if isinstance(uid_or_obj, dict):
            return self._ref(uid_or_obj)
        return self._objects.get(uid_or_obj, {"uid": uid_or_obj, "className": "", "type": ""})

    # ---- oturum ---------------------------------------------------------
    def login(self, user: str, password: str, group: str = "", role: str = "") -> None:
        self.call("Core-2011-06-Session/login", {"credentials": {
            "user": user, "password": password, "group": group, "role": role,
            "locale": "", "descrimator": "tc_rev_compare"}})

    def logout(self) -> None:
        try:
            self.call("Core-2006-03-Session/logout", {})
        except TeamcenterError as e:
            log.debug("logout: %s", e)

    # ---- veri -----------------------------------------------------------
    def get_properties(self, objs: list, names: list[str], chunk: int = 500) -> dict[str, dict[str, str]]:
        """{uid: {özellik: değer}} döner; değer ilk dolu dbValue/uiValue'dur."""
        out: dict[str, dict[str, str]] = {}
        refs = [self.ref(o) for o in objs]
        for i in range(0, len(refs), chunk):
            data = self.call("Core-2006-03-DataManagement/getProperties",
                             {"objects": refs[i:i + chunk], "attributes": names})
            for msg in _service_errors(data)[:5]:
                log.warning("getProperties: %s", msg)
            for uid, mo in ((data.get("ServiceData") or {}).get("modelObjects") or {}).items():
                props = out.setdefault(uid, {})
                for name, val in (mo.get("props") or {}).items():
                    props[name] = _first_value(val)
                    if name.endswith("_list"):
                        props[name + "@all"] = val.get("dbValues") or []
        return out

    def find_item(self, item_id: str) -> dict:
        data = self.call("Core-2007-01-DataManagement/getItemFromId", {
            "infos": [{"itemId": item_id, "revIds": []}], "nRev": 0, "pref": {"prefs": []}})
        for out in data.get("output") or []:
            if not _is_null(out.get("item")):
                return self.ref(out["item"])
        raise TeamcenterError(f"Item bulunamadı: {item_id} {'; '.join(_service_errors(data))}")

    def revisions(self, item: dict) -> list[Revision]:
        props = self.get_properties([item], ["revision_list"]).get(item["uid"], {})
        uids = props.get("revision_list@all") or []
        if not uids:
            raise TeamcenterError("Item'ın revizyon listesi okunamadı")
        rp = self.get_properties(uids, ["item_revision_id", "object_name", "creation_date",
                                        "date_released", "release_status_list"])
        revs = []
        for uid in uids:
            p = rp.get(uid, {})
            revs.append(Revision(self.ref(uid), p.get("item_revision_id", ""), p.get("object_name", ""),
                                 p.get("creation_date", ""), p.get("date_released", ""),
                                 [s for s in p.get("release_status_list@all", []) if s]))
        return revs

    def revision_rule(self, name: str) -> dict:
        if not name:
            return NULL_REF
        data = self.call("Cad-2007-01-StructureManagement/getRevisionRules", {})
        rules = [o["revRule"] for o in data.get("output") or [] if not _is_null(o.get("revRule"))]
        names = self.get_properties(rules, ["object_name"])
        for r in rules:
            if names.get(r["uid"], {}).get("object_name", "").strip().lower() == name.strip().lower():
                return self.ref(r)
        available = ", ".join(sorted(v.get("object_name", "") for v in names.values()))
        raise TeamcenterError(f"Revizyon kuralı bulunamadı: '{name}'. Mevcut kurallar: {available}")

    def load_bom(self, item: dict, rev: dict, rule: dict, date: datetime | None = None,
                 extra_props: list[str] | None = None) -> BomNode:
        """BOM'u tüm seviyeleriyle açar. extra_props ham değerleri node.extra'ya yazılır."""
        rule_props = {"unitNo": -1, "today": date is None, "endItem": NULL_REF,
                      "endItemRevision": NULL_REF, "overrideFolders": []}
        if date is not None:
            rule_props["date"] = date.strftime("%Y-%m-%dT%H:%M:%S") + "+00:00"
        data = self.call("Cad-2007-01-StructureManagement/createBOMWindows", {"info": [{
            "clientId": "tc_rev_compare", "item": item, "itemRev": rev, "bomView": NULL_REF,
            "revRuleConfigInfo": {"clientId": "", "revRule": rule, "props": rule_props},
            "objectForConfigure": NULL_REF, "activeAssemblyArrangement": NULL_REF}]})
        outs = data.get("output") or []
        if not outs or _is_null(outs[0].get("bomLine")):
            raise TeamcenterError("BOM penceresi açılamadı: " + "; ".join(_service_errors(data)))
        window, root = self.ref(outs[0]["bomWindow"]), self.ref(outs[0]["bomLine"])
        try:
            exp = self.call("Cad-2008-06-StructureManagement/expandPSAllLevels", {
                "input": {"parentBomLines": [root], "excludeFilter": "None"},
                "pref": {"expItemRev": False, "info": []}})
            children: dict[str, list[str]] = {}
            for o in exp.get("output") or []:
                parent = o["parent"]["bomLine"]["uid"]
                children[parent] = [c["bomLine"]["uid"] for c in o.get("children") or []]
                for c in o.get("children") or []:
                    self._objects.setdefault(c["bomLine"]["uid"], self._ref(c["bomLine"]))
            all_lines = [root["uid"]] + [u for kids in children.values() for u in kids]
            log.info("%d BOM satırı okunuyor...", len(all_lines))
            names = list(dict.fromkeys(self.cfg.bomline_properties() + (extra_props or [])))
            props = self.get_properties(all_lines, names)
            return self._build(root["uid"], children, props, extra_props or [])
        finally:
            try:
                self.call("Cad-2007-01-StructureManagement/closeBOMWindows", {"bomWindows": [window]})
            except TeamcenterError as e:
                log.debug("closeBOMWindows: %s", e)

    def _build(self, uid: str, children: dict[str, list[str]], props: dict[str, dict[str, str]],
               extra: list[str]) -> BomNode:
        p = props.get(uid, {})
        node = self.cfg.node_from_props(p)
        node.extra = {k: p.get(k, "") for k in extra}
        for c in children.get(uid, []):
            node.add(self._build(c, children, props, extra))
        return node


def _first_value(val: dict) -> str:
    for key in ("dbValues", "uiValues"):
        for v in val.get(key) or []:
            if v not in (None, ""):
                return str(v)
    return ""


def latest_revision(revs: list[Revision], released_only: bool) -> Revision:
    pool = [r for r in revs if r.statuses or r.released] if released_only else revs
    if not pool:
        raise TeamcenterError("Yayınlanmış (released) revizyon bulunamadı")
    # revision_list oluşturulma sırasındadır; son eleman en günceldir
    return pool[-1]

