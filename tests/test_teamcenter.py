"""Teamcenter JSON-REST akışını sahte bir sunucuyla uçtan uca dener."""

import json

import pytest

from tc_rev_compare import cli
from tc_rev_compare.teamcenter import NULL_REF

M = "1 0 0 0 0 1 0 0 0 0 1 0 {x} {y} {z} 1"  # metre

# uid -> özellikler
OBJS = {
    "item": {"revision_list": ["revA", "revB", "revC"]},
    "revA": {"item_revision_id": "A", "object_name": "MONTAJ", "creation_date": "2021-03-01T10:00:00+03:00"},
    "revB": {"item_revision_id": "B", "object_name": "MONTAJ", "creation_date": "2023-01-01T10:00:00+03:00"},
    "revC": {"item_revision_id": "C", "object_name": "MONTAJ", "creation_date": "2026-09-01T10:00:00+03:00"},
    "rule1": {"object_name": "Latest Working"},
}
BOMS = {
    "revA": [("A0", None, "100", "A", "MONTAJ", "", "", ""),
             ("A1", "A0", "200", "A", "BRAKET", "10", "1.5", M.format(x=0.1, y=0, z=0)),
             ("A2", "A0", "300", "A", "PLAKA", "20", "4", M.format(x=0, y=0, z=0))],
    "revC": [("C0", None, "100", "C", "MONTAJ", "", "", ""),
             ("C1", "C0", "200", "B", "BRAKET YENİ", "10", "1.7", M.format(x=0.125, y=0, z=0)),
             ("C2", "C0", "300", "A", "PLAKA", "20", "4", M.format(x=0, y=0, z=0))],
}
for rows in BOMS.values():
    for uid, _, item_id, rev, name, find, w, xf in rows:
        OBJS[uid] = {"bl_item_item_id": item_id, "bl_rev_item_revision_id": rev, "bl_rev_object_name": name,
                     "bl_sequence_no": find, "bl_rev_weight": w, "bl_plmxml_occ_xform": xf}


def ref(uid):
    return {"uid": uid, "className": "X", "type": "X"}


class FakeResponse:
    status_code = 200

    def __init__(self, data):
        self._data = data
        self.text = json.dumps(data)

    def json(self):
        return self._data


def fake_post(self, url, json=None, timeout=None):
    service = url.split("JsonRestServices/")[1]
    body = json["body"]
    calls.append(service)
    if service.endswith("/login") or service.endswith("/logout") or service.endswith("/closeBOMWindows"):
        return FakeResponse({})
    if service.endswith("/getItemFromId"):
        assert body["infos"][0]["itemId"] == "100"
        return FakeResponse({"output": [{"item": ref("item")}]})
    if service.endswith("/getProperties"):
        mos = {}
        for o in body["objects"]:
            props = OBJS[o["uid"]]
            mos[o["uid"]] = {"uid": o["uid"], "className": "X", "type": "X", "props": {
                a: {"dbValues": v if isinstance(v, list) else [v]} for a, v in props.items() if a in body["attributes"]}}
        return FakeResponse({"ServiceData": {"modelObjects": mos}})
    if service.endswith("/getRevisionRules"):
        return FakeResponse({"output": [{"revRule": ref("rule1")}]})
    if service.endswith("/createBOMWindows"):
        info = body["info"][0]
        assert info["revRuleConfigInfo"]["revRule"]["uid"] == "rule1"
        assert info["bomView"] == NULL_REF
        rev = info["itemRev"]["uid"]
        return FakeResponse({"output": [{"bomWindow": ref("win"), "bomLine": ref(BOMS[rev][0][0])}]})
    if service.endswith("/expandPSAllLevels"):
        root = body["input"]["parentBomLines"][0]["uid"]
        rows = next(r for r in BOMS.values() if r[0][0] == root)
        kids = [ref(r[0]) for r in rows if r[1] == root]
        return FakeResponse({"output": [{"parent": {"bomLine": ref(root)},
                                         "children": [{"bomLine": k} for k in kids]}]})
    raise AssertionError(service)


calls: list[str] = []


def test_end_to_end(monkeypatch, tmp_path, capsys):
    monkeypatch.setattr("requests.Session.post", fake_post)
    monkeypatch.setenv("TC_USER", "u")
    monkeypatch.setenv("TC_PASSWORD", "p")
    cfg = tmp_path / "ayarlar.toml"
    cfg.write_text('[teamcenter]\nurl = "http://x/tc"\nrevizyon_kurali = "Latest Working"\n', encoding="utf-8")
    out = tmp_path / "r.xlsx"
    assert cli.main(["100/A", "--ayarlar", str(cfg), "--cikti", str(out)]) == 0
    text = capsys.readouterr().out
    assert "5.500 kg  ->  5.700 kg" in text
    assert "'BRAKET'  ->  'BRAKET YENİ'" in text
    assert "25.0 mm" in text
    assert out.exists()
    assert calls.count("Cad-2007-01-StructureManagement/closeBOMWindows") == 2
    assert calls[-1] == "Core-2006-03-Session/logout"


def test_unknown_revision(monkeypatch, tmp_path, caplog):
    monkeypatch.setattr("requests.Session.post", fake_post)
    monkeypatch.setenv("TC_USER", "u")
    monkeypatch.setenv("TC_PASSWORD", "p")
    assert cli.main(["100/Z", "--ayarlar", str(tmp_path / "yok.toml")]) == 2
    assert "Mevcut: A, B, C" in caplog.text
