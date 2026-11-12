#!/usr/bin/env python3
"""采集公开OL开发资料，或离线核对已采集包；不登录、不冻结正式来源。"""
import argparse
import hashlib
import html
import json
from pathlib import Path
import re
import struct
import subprocess
import tempfile
from datetime import datetime, timezone
from urllib.parse import urljoin, urlparse

SITE = "https://www.sanguosha.com/"
PC = "https://web.sanguosha.com/220/h5_2/"
GENERALS = {"guanyu": (2, "关羽"), "zhaoyun": (5, "赵云"),
            "zhangliao": (18, "张辽"), "huatuo": (22, "华佗"),
            "caocao": (15, "曹操"), "lvbu": (23, "吕布")}
ATLASES = ["baseUI/baseUI", "cards/normal/cards", "game/gameBase",
           "game/seat", "game/seatbottom", "game/seatState", "game/selfseat", "table/table"]


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def clean_text(value):
    return html.unescape(re.sub(r"<[^>]+>", "", value)).strip()


def parse_skills(page):
    names = re.findall(r'<div class="character-tab(?:\s[^"]*)?"[^>]*>(.*?)</div>', page, re.S)
    block = re.search(r'<p class="skill-text">(.*?)</p>', page, re.S)
    texts = [] if block is None else re.findall(r'<span[^>]*>(.*?)</span>', block[1], re.S)
    if not names or len(names) != len(texts):
        raise ValueError("官网技能结构发生变化，停止采集，避免遗漏或错配。")
    return [{"name": clean_text(n), "text": clean_text(t)} for n, t in zip(names, texts)]


def png_size(data):
    if len(data) < 24 or data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        raise ValueError("预期PNG原图，实际返回了其他内容。")
    return list(struct.unpack(">II", data[16:24]))


def safe_path(base, relative):
    path = Path(relative)
    if path.is_absolute() or any(p in ("", ".", "..") for p in relative.split("/")):
        raise ValueError(f"非法相对路径：{relative}")
    current = base
    for part in path.parts:
        current = current / part
        if current.is_symlink():
            raise ValueError(f"不接受符号链接：{relative}")
    return current


def fetch(url, path):
    if urlparse(url).scheme != "https" or urlparse(url).hostname not in {
        "www.sanguosha.com", "web.sanguosha.com", "oldownload.sanguosha.com"
    }:
        raise ValueError(f"不是本采集器登记的官方HTTPS来源：{url}")
    path.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["curl", "--fail", "--location", "--silent", "--show-error",
                    "--proto", "=https", "--proto-redir", "=https",
                    "--max-time", "30", "--max-filesize", "16777216",
                    url, "--output", str(path)], check=True)
    return path.read_bytes()


def collect(destination):
    if destination.exists():
        raise ValueError("采集目录已存在；新批次请使用新的目录，保留原始快照。")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ol-capture-", dir=destination.parent) as tmp:
        staging = Path(tmp)
        catalog = {"formatVersion": 1, "sourceGame": "sgs_online",
                   "capturedAt": datetime.now(timezone.utc).isoformat(),
                   "status": "development_reference", "clientVersion": None,
                   "usage": "开发参照；不随当前运行包分发，公开分发范围待确认。",
                   "files": [], "generals": [], "atlases": [],
                   "limitations": ["官网与PC静态资源未证明属于同一客户端版本。",
                                   "历史战斗截图仅作布局参考，不是当前快照。",
                                   "缺少十类操作状态的同版本截图与逐步录像。"]}

        def download(relative, url, kind, **metadata):
            data = fetch(url, staging / relative)
            entry = {"path": relative, "sourceUrl": url, "kind": kind,
                     "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data), **metadata}
            if relative.endswith(".png"):
                entry["dimensions"] = png_size(data)
            catalog["files"].append(entry)
            print(f"已采集 {relative} ({len(data)} bytes)", flush=True)
            return data

        index = download("official/hero.html", urljoin(SITE, "hero"), "general_index").decode("utf-8")
        match = re.search(r"const\s+Listdata\s*=\s*", index)
        if match is None:
            raise ValueError("官网武将索引格式已变化。")
        groups, _ = json.JSONDecoder().raw_decode(index[match.end():])
        standard = {g["gid"]: g for g in groups["标准"]}
        for local_id, (gid, name) in GENERALS.items():
            source = standard[gid]
            if source["name"] != name or source["extend"] != "标准":
                raise ValueError(f"标准武将映射不一致：{local_id}")
            page_path = f"official/hero-{gid}.html"
            page = download(page_path, urljoin(SITE, f"hero/{gid}"), "general").decode("utf-8")
            portrait = f"portraits/{local_id}-{source['resource']}.png"
            # 保留官网明确引用的原图路径；不把移动资源目录冒记为PC实测证据。
            download(portrait, source["cover"], "portrait", generalId=local_id)
            catalog["generals"].append({"generalId": local_id, "displayName": name,
                "officialGeneralId": gid, "variant": "标准", "resourceId": source["resource"],
                "maxHp": source["hp"], "faction": source["figure"],
                "gender": None, "skills": parse_skills(page),
                "pagePath": page_path, "portraitPath": portrait,
                "includedInRuntime": False})

        version_bytes = download("pc/version.json", urljoin(PC, "version.json"), "resource_version_index")
        versions = json.loads(version_bytes)
        for group in ATLASES:
            atlas_path = f"res/assets/{group}.atlas"
            image_path = f"res/assets/{group}.png"
            for path, kind in [(atlas_path, "atlas"), (image_path, "ui_texture")]:
                download("pc/" + path, urljoin(PC, path), kind, sourceVersion=versions[path])
            atlas = read_json(staging / "pc" / atlas_path)
            catalog["atlases"].append({"atlasPath": "pc/" + atlas_path,
                "imagePath": "pc/" + image_path, "frameCount": len(atlas["frames"])})

        guide = "news/20180906_4914_1519"
        download("official/basic-card-guide.html", urljoin(SITE, guide), "historical_guide")
        download("reference/battle-20180906.jpg", urljoin(SITE, "uploads/201809/5b910d22a9912.jpg"),
                 "historical_battle_screenshot", publishedAt="2018-09-06")
        download("official/download-config.json", "https://oldownload.sanguosha.com/download/config.json",
                 "launcher_version_config")
        # 版本表中的数值是官方不透明标识，不冒充SHA或客户端版本。
        with tempfile.TemporaryDirectory() as check_dir:
            after = fetch(urljoin(PC, "version.json"), Path(check_dir) / "version.json")
        if json.loads(after) != versions:
            raise ValueError("采集过程中官方版本索引改变，请使用新批次重试。")
        write_json(staging / "catalog.json", catalog)
        verify(staging)
        staging.rename(destination)


def verify(directory):
    catalog = read_json(directory / "catalog.json")
    if catalog["sourceGame"] != "sgs_online" or not catalog["files"]:
        raise ValueError("无效OL采集目录。")
    entries = {}
    for item in catalog["files"]:
        path = item["path"]
        if path in entries:
            raise ValueError(f"重复文件：{path}")
        entries[path] = item
        data = safe_path(directory, path).read_bytes()
        if len(data) != item["bytes"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError(f"文件摘要或长度不符：{path}")
        if "dimensions" in item and png_size(data) != item["dimensions"]:
            raise ValueError(f"图片尺寸不符：{path}")
    versions = read_json(directory / "pc/version.json")
    for item in entries.values():
        if "sourceVersion" in item and versions[item["path"].removeprefix("pc/")] != item["sourceVersion"]:
            raise ValueError(f"资源索引不符：{item['path']}")
    for group in catalog["atlases"]:
        atlas = read_json(safe_path(directory, group["atlasPath"]))
        width, height = entries[group["imagePath"]]["dimensions"]
        if len(atlas["frames"]) != group["frameCount"]:
            raise ValueError("图集部件数不符。")
        if atlas["meta"]["image"] != Path(group["imagePath"]).name:
            raise ValueError("图集图片引用不符。")
        for name, value in atlas["frames"].items():
            frame = value["frame"]
            if (frame.get("idx", 0) != 0 or min(frame[k] for k in ("x", "y", "w", "h")) < 0
                    or frame["x"] + frame["w"] > width or frame["y"] + frame["h"] > height):
                raise ValueError(f"图集坐标越界或引用其他图片：{name}")
    if (len(catalog["generals"]) != len(GENERALS)
            or {g["generalId"] for g in catalog["generals"]} != set(GENERALS)):
        raise ValueError("六名MVP人物缺失或混入其他人物。")
    index = safe_path(directory, "official/hero.html").read_text(encoding="utf-8")
    match = re.search(r"const\s+Listdata\s*=\s*", index)
    if match is None:
        raise ValueError("缺少官网武将索引。")
    groups, _ = json.JSONDecoder().raw_decode(index[match.end():])
    standard = {g["gid"]: g for g in groups["标准"]}
    for general in catalog["generals"]:
        gid, name = GENERALS[general["generalId"]]
        source = standard[gid]
        if (general["officialGeneralId"] != gid or general["displayName"] != name
                or general["variant"] != "标准" or source["extend"] != "标准"
                or general["maxHp"] != source["hp"] or general["faction"] != source["figure"]
                or general["resourceId"] != source["resource"] or general["includedInRuntime"]):
            raise ValueError(f"标准武将属性映射或开发范围不符：{name}")
        page = safe_path(directory, general["pagePath"]).read_text(encoding="utf-8")
        if (parse_skills(page) != general["skills"]
                or entries[general["portraitPath"]]["sourceUrl"] != source["cover"]
                or entries[general["pagePath"]]["sourceUrl"] != urljoin(SITE, f"hero/{gid}")):
            raise ValueError(f"技能或立绘引用不一致：{general['generalId']}")
    print(f"OL采集包校验通过：{len(entries)}份文件，{len(catalog['generals'])}名武将，"
          f"{sum(a['frameCount'] for a in catalog['atlases'])}个图集部件。")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="采集包目录；下载时必须为新目录")
    parser.add_argument("--fetch", action="store_true", help="联网采集；默认只离线校验")
    args = parser.parse_args()
    try:
        (collect if args.fetch else verify)(args.directory.resolve())
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"OL采集失败：{error}\n")


if __name__ == "__main__":
    main()
