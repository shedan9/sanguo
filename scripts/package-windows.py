#!/usr/bin/env python3
"""核验G0导出、收集声明和验收说明、生成文件摘要及压缩包。"""
import hashlib
import json
import pathlib
import shutil
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "build/windows"
MANAGED = OUTPUT / "data_Sanguo.Client_windows_x86_64"
LOG = ROOT / "build/evidence/g0/windows-export.log"


def package():
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
    options = json.loads((MANAGED / "Sanguo.Client.runtimeconfig.json").read_text())["runtimeOptions"]
    if options.get("tfm") != "net10.0" or not options.get("includedFrameworks"):
        raise ValueError("缺少预期的.NET 10自包含运行时。")

    licenses = OUTPUT / "licenses"
    licenses.mkdir(exist_ok=True)
    for path in (ROOT / "third-party").iterdir():
        if path.is_file():
            shutil.copy2(path, licenses / path.name)
    (OUTPUT / "验收说明.txt").write_text(
        "三国行纪 G0 Windows x64内部验证包\n\n"
        "范围：固定种子抽牌、保存和恢复。不是P0战斗原型，无OL武将或素材。\n"
        "请复制整个目录，保留data_Sanguo.Client_windows_x86_64及licenses。\n"
        "包含.NET 10运行时；无开发SDK设备能否运行仍待Windows真机确认。\n\n"
        "人工验证：\n"
        "1. 在没有开发SDK的Windows 11 x64设备离线启动Sanguo.exe。\n"
        "2. 检查中文、四个按钮和Tab焦点。抽3张保存，抽第4张，再恢复应回到3张。\n"
        "3. 重启后恢复同一存档。记录设备、系统、分辨率和结果。\n"
        "4. 在PowerShell执行下行，检查ExitCode为0和g0-smoke.log中的哈希：\n"
        "Start-Process .\\Sanguo.exe -ArgumentList '--headless --log-file g0-smoke.log -- --smoke' -Wait -PassThru\n"
        "预期日志包含：\n"
        "G0_SMOKE_OK 066d2413bb3ac61446d293248828c47eb1e704b0e930dea0e1775b28f1aaa883\n\n"
        "本包只在macOS交叉导出，尚未执行Windows真机验收。\n", encoding="utf-8")
    hashes = []
    for path in sorted(OUTPUT.rglob("*")):
        if path.is_file() and path.name != "package-manifest.json":
            hashes.append({"path": path.relative_to(OUTPUT).as_posix(), "bytes": path.stat().st_size,
                           "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    manifest = {"scope": "g0-probe", "engine": "0.1.0-g0", "godot": "4.7.2",
                "sdk": "10.0.404", "target": "win-x64", "builtOn": "macOS-arm64",
                "testedOnWindows": False, "files": hashes}
    (OUTPUT / "package-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    archive = ROOT / "build/sanguo-g0-windows-x64.zip"
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
