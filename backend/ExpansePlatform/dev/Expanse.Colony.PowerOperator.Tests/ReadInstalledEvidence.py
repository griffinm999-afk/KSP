"""Read exact installed cache/craft bytes; no game calls or mutations."""
import hashlib
import json
from pathlib import Path
import sys

workspace = Path(__file__).resolve().parents[3]
platform = workspace / "ExpansePlatform"
audit = workspace / "outputs/colony-runtime-tests/end-to-end-origin/audit.py"
scope = {"__file__": str(audit)}
# Reuse only its bounded syntax reader, never its artifact-writing main body.
exec(audit.read_text().split("data = SAVE.read_bytes()")[0], scope)
parse, children, descendants, value = (scope[k] for k in ("parse", "children", "descendants", "value"))
cache = Path("C:/Kerbal Space Program/GameData/ModuleManager.ConfigCache")
craft = platform / "package/GameData/ExpanseWorldBridge/Templates/power-duna-v1.craft"
manifest = craft.with_suffix(".manifest.json")
part = [n for n in descendants(parse(cache.read_text(encoding="utf-8-sig"))) if n["tag"] == "PART" and value(n,"name") == "Duna_PDU"]
if len(part) != 1:
    raise ValueError("Installed exact Duna_PDU is absent or duplicated")
craft_root = parse(craft.read_text(encoding="utf-8-sig"))
craft_parts = children(craft_root, "PART")
mapped = [p for p in craft_parts if value(p,"part") == "Duna.PDU_104"]
terms = json.loads(manifest.read_text(encoding="utf-8-sig"))
hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (cache, craft, manifest)}
data = {
    "Authority": "Read-only installed metadata and reviewed craft lineage; no native activation, crew, power or thermal proof",
    "Sha256": hashes,
    "TemplateId": terms["Id"], "DeclaredWorkers": terms["Workers"], "DeclaredTrait": terms["WorkerTrait"],
    "ReviewedCraftSha256": terms["CraftSha256"], "ActualCraftSha256": hashes[str(craft)],
    "ExpectedPartCount": terms["ExpectedPartCount"], "ActualCraftPartCount": len(craft_parts),
    "OperatorCraftId": 104, "OperatorCraftMappingCount": len(mapped),
    "NativePartName": value(part[0],"name"), "RuntimePartName": "Duna.PDU", "NativeCrewCapacity": value(part[0],"CrewCapacity"),
    "InstalledModules": [{"Name":value(m,"name"),"Values":m["values"]} for m in children(part[0],"MODULE")],
}
print(json.dumps(data, indent=2))
