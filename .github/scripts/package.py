"""Zips the published plugin and adds the version to manifest.json (Jellyfin plugin repository)."""
import datetime
import hashlib
import json
import os
import zipfile

version = os.environ["VERSION"]
repo = os.environ["GITHUB_REPOSITORY"]
now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

meta_path = "out/meta.json"
meta = json.load(open(meta_path, encoding="utf-8"))
meta["version"] = version + ".0"
meta["timestamp"] = now
json.dump(meta, open(meta_path, "w", encoding="utf-8"), indent=2)

os.makedirs("dist", exist_ok=True)
zip_path = f"dist/catchup_{version}.zip"
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    z.write("out/Jellyfin.Plugin.Catchup.dll", "Jellyfin.Plugin.Catchup.dll")
    z.write(meta_path, "meta.json")

entry = {
    "version": version + ".0",
    "changelog": f"https://github.com/{repo}/releases/tag/v{version}",
    "targetAbi": meta["targetAbi"],
    "sourceUrl": f"https://github.com/{repo}/releases/download/v{version}/catchup_{version}.zip",
    "checksum": hashlib.md5(open(zip_path, "rb").read()).hexdigest(),
    "timestamp": now,
}
manifest = json.load(open("manifest.json", encoding="utf-8"))
manifest[0]["versions"] = [entry] + [e for e in manifest[0]["versions"] if e["version"] != entry["version"]]
json.dump(manifest, open("manifest.json", "w", encoding="utf-8"), indent=2)
print(zip_path, entry["checksum"])
