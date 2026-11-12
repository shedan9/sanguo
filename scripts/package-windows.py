#!/usr/bin/env python3
"""核验G0导出、收集声明和验收说明、生成文件摘要及压缩包。"""
import hashlib
import json
import pathlib
import shutil
import struct
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def package(root=ROOT):
    OUTPUT = root / "build/windows"
    MANAGED = OUTPUT / "data_Sanguo.Client_windows_x86_64"
    LOG = root / "build/evidence/g0/windows-export.log"
    if not LOG.exists() or "ERROR:" in LOG.read_text():
        raise ValueError("导出日志缺失或含错误，请查看build/evidence/g0/windows-export.log。")
    required = [OUTPUT / "Sanguo.exe"] + [MANAGED / name for name in (
        "Sanguo.Client.dll", "Sanguo.Application.dll", "Sanguo.BattleCore.dll",
        "Sanguo.CampaignCore.dll", "Sanguo.Content.dll", "Sanguo.Infrastructure.dll",
        "Sanguo.Client.runtimeconfig.json", "coreclr.dll", "hostfxr.dll")]
    for path in required:
        if not path.is_file() or path.stat().st_size == 0:
            raise ValueError("缺少导出文件：" + str(path.relative_to(OUTPUT)))
    with (OUTPUT / "Sanguo.exe").open("rb") as executable:
        if executable.read(2) != b"MZ":
            raise ValueError("导出的文件不是Windows可执行文件。")
        executable.seek(0x3C)
        offset = executable.read(4)
        if len(offset) != 4:
            raise ValueError("Windows可执行文件缺少PE头偏移。")
        executable.seek(struct.unpack("<I", offset)[0])
        if executable.read(6) != b"PE\x00\x00\x64\x86":
            raise ValueError("导出的文件不是Windows x64 PE程序。")
    options = json.loads((MANAGED / "Sanguo.Client.runtimeconfig.json").read_text())["runtimeOptions"]
    if options.get("tfm") != "net10.0" or not options.get("includedFrameworks"):
        raise ValueError("缺少预期的.NET 10自包含运行时。")

    licenses = OUTPUT / "licenses"
    licenses.mkdir(exist_ok=True)
    for path in (root / "third-party").iterdir():
        if path.is_file():
            shutil.copy2(path, licenses / path.name)
    shutil.copy2(root / "scripts/verify-windows.ps1", OUTPUT / "verify-windows.ps1")
    (OUTPUT / "验收说明.txt").write_text(
        "三国行纪 G0 Windows x64内部验证包\n\n"
        "范围：固定种子抽牌、保存和恢复。不是P0战斗原型，无OL武将或素材。\n"
        "请复制整个目录，保留data_Sanguo.Client_windows_x86_64及licenses。\n"
        "包含.NET 10运行时；无开发SDK设备能否运行仍待Windows真机确认。\n\n"
        "人工验证：\n"
        "1. 在没有开发SDK的Windows 11 x64设备离线启动Sanguo.exe。\n"
        "2. 检查中文、四个按钮和Tab焦点。抽3张保存，抽第4张，再恢复应回到3张。\n"
        "3. 重启后恢复同一存档。记录设备、系统、分辨率和结果。\n"
        "4. 在包目录打开PowerShell，执行以下自动验收（仅对本次进程放行脚本）：\n"
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\verify-windows.ps1\n"
        "自动核对文件摘要、固定哈希、两个独立进程保存恢复、命令去重及损坏档备份回退。\n"
        "日志、专用试验档及acceptance-report.json保存在输出的临时目录；请将整个目录归档。\n"
        "请另附设备、离线启动和窗口人工验证记录；自动检查不表示G0通过。\n"
        "若检测到开发SDK，保留结果但返回失败，需换干净目标设备复验。\n"
        "预期日志包含：\n"
        "G0_SMOKE_OK 066d2413bb3ac61446d293248828c47eb1e704b0e930dea0e1775b28f1aaa883\n\n"
        "本包只在macOS交叉导出，尚未执行Windows真机验收。\n", encoding="utf-8")
    hashes = []
    for path in sorted(OUTPUT.rglob("*")):
        if path.is_symlink():
            raise ValueError("验证包不能包含符号链接：" + str(path.relative_to(OUTPUT)))
        if path.is_file() and path != OUTPUT / "package-manifest.json":
            hashes.append({"path": path.relative_to(OUTPUT).as_posix(), "bytes": path.stat().st_size,
                           "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    manifest = {"scope": "g0-probe", "engine": "0.1.0-g0", "godot": "4.7.2",
                "sdk": "10.0.404", "target": "win-x64", "builtOn": "macOS-arm64",
                "testedOnWindows": False, "files": hashes}
    (OUTPUT / "package-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    archive = root / "build/sanguo-g0-windows-x64.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
        for path in sorted(OUTPUT.rglob("*")):
            if path.is_file():
                output.write(path, "sanguo-g0-windows-x64/" + path.relative_to(OUTPUT).as_posix())
    print("Windows验证包结构通过，含自包含运行时：", archive)
    print("Windows真机验收：待执行。")


if __name__ == "__main__":
    try:
        package()
    except (ValueError, KeyError, OSError) as error:
        print("打包失败：" + str(error), file=sys.stderr)
        sys.exit(1)
