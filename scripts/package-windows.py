#!/usr/bin/env python3
"""核验G0导出、收集声明和验收说明、生成文件摘要及压缩包。"""
import hashlib
import json
import pathlib
import platform
import shutil
import struct
import sys
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def package(root=ROOT):
    OUTPUT = root / "build/windows"
    MANAGED = OUTPUT / "data_Sanguo.Client_windows_x86_64"
    LOG = root / "build/evidence/g0/windows-export.log"
    if not LOG.exists():
        raise ValueError("导出日志缺失或含错误，请查看build/evidence/g0/windows-export.log。")
    log = LOG.read_text(encoding="utf-8")
    certificate_error = "ERROR: Failed to read the root certificate store."
    export_warnings = []
    if platform.system() == "Windows" and log.count(certificate_error) == 1:
        log = log.replace(certificate_error, "", 1)
        export_warnings.append("sandbox-system-ca-store-unavailable")
    if "ERROR:" in log:
        raise ValueError("导出日志缺失或含错误，请查看build/evidence/g0/windows-export.log。")
    required = [OUTPUT / "Sanguo.exe"] + [MANAGED / name for name in (
        "Sanguo.Client.dll", "Sanguo.AI.dll", "Sanguo.Application.dll", "Sanguo.BattleCore.dll",
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
    built_on = platform.system() + "-" + ("x64" if platform.machine().lower() in {"amd64", "x86_64"} else platform.machine().lower())
    (OUTPUT / "验收说明.txt").write_text(
        "三国行纪 G0 Windows x64内部验证包\n\n"
        "范围：G0固定种子抽牌、双方各1至4人的战斗开发版（默认2对2）及三线首城开发行程；完整游戏、P0与OL视觉还原未完成。\n"
        "美术：内嵌已采集的关羽、曹操立绘与PC武将框、八种卡面和按钮图集，仅供内部开发验证；正式人物内容未接入，公开分发范围待确认。\n"
        "请复制整个目录，保留data_Sanguo.Client_windows_x86_64及licenses。\n"
        "包含.NET 10运行时；无开发SDK设备能否运行仍待Windows真机确认。\n\n"
        "人工验证：\n"
        "1. 在没有开发SDK的Windows 11 x64设备离线启动Sanguo.exe。\n"
        "2. 通过新局弹层中的G0抽牌试验按钮进入抽样场景，用鼠标检查抽牌/保存/恢复/新建按钮。抽3张保存，抽第4张，再恢复应回到3张。\n"
        "3. 重启后恢复同一存档。记录设备、系统、分辨率和结果。\n"
        "战斗检查：调整人数并新建，确认中央公开卡面、来源目标、反制层数和底栏伤害/技能提示。\n"
        "用鼠标点手牌、点目标并确定；技能先点技能按钮，奋击成本再点另一张手牌，无动作方案列表。\n"
        "悬停手牌应平滑抬升放大；拖动可整理手牌（灰置牌也可移动），拖向发光目标可松手出牌；自用/响应牌拖入中央出牌区。\n"
        "无效放置应回弹且不扣牌；确认飞牌、牌背摸牌、命中斩痕/伤害飘字、回复光环及技能/回合反馈；读档后不重播旧动画。\n"
        "手牌与处理区可查看完整牌面和用途；顶部手牌按钮打开只读一览。设置支持缩放和减少动态，后者停用飞牌与粒子。\n"
        "拖牌靠近手牌区两侧应自动滚动；切换友军保留各自排序；右键、Esc或窗口失焦可收回拖牌，不消耗牌。\n"
        "终局显示实际伤害、回复和存活统计，可再来一局或返回行程。新局弹层中的首城行程可进入三条开发路线，战役保存包含当前牌局。\n"
        "金色流动指向线从选中牌指向目标，取消即清除，出牌后短暂淡出；检查目标红框与回合绿框。\n"
        "弃牌时多选或一键选择，选满所需数量才能确定；取消不扣牌。整理手牌和侧栏切换/收起不推进战斗。\n"
        "无懈无法响应时略过；有牌时点无懈后确定，或点不出/本次锦囊不再询问；拆顺在弹层中点牌背。\n"
        "打开资料或记录，确认敌方暂停；点击关闭后原请求继续。敌方手牌只显示数量。\n"
        "4. 在包目录打开PowerShell，执行以下自动验收（仅对本次进程放行脚本）：\n"
        "powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\verify-windows.ps1\n"
        "自动核对文件摘要、固定哈希、两个独立进程保存恢复、命令去重及损坏档备份回退。\n"
        "日志、专用试验档及acceptance-report.json保存在输出的临时目录；请将整个目录归档。\n"
        "请另附设备、离线启动和窗口人工验证记录；自动检查不表示G0通过。\n"
        "若检测到开发SDK，保留结果但返回失败，需换干净目标设备复验。\n"
        "预期日志包含：\n"
        "G0_SMOKE_OK 066d2413bb3ac61446d293248828c47eb1e704b0e930dea0e1775b28f1aaa883\n\n"
        f"本包在{built_on}构建，尚未执行独立Windows真机验收。\n", encoding="utf-8")
    hashes = []
    for path in sorted(OUTPUT.rglob("*")):
        if path.is_symlink():
            raise ValueError("验证包不能包含符号链接：" + str(path.relative_to(OUTPUT)))
        if path.is_file() and path != OUTPUT / "package-manifest.json":
            hashes.append({"path": path.relative_to(OUTPUT).as_posix(), "bytes": path.stat().st_size,
                           "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    manifest = {"scope": "g0-probe", "engine": "0.1.0-g0", "godot": "4.7.2",
                "developmentScopes": ["g0-probe", "client-battle-development.v1", "campaign.first-city-development.v1"],
                "artworkUsage": "internal-development-only",
                "sdk": "10.0.404", "target": "win-x64", "builtOn": built_on,
                "testedOnWindows": False, "exportWarnings": export_warnings, "files": hashes}
    (OUTPUT / "package-manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    archive = root / "build/sanguo-g0-windows-x64.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
        for path in sorted(OUTPUT.rglob("*")):
            if path.is_file():
                output.write(path, "sanguo-g0-windows-x64/" + path.relative_to(OUTPUT).as_posix())
    print("Windows验证包结构通过，含自包含运行时：", archive)
    if export_warnings:
        print("构建环境警告：无法读取系统根证书库；完整日志保留在build/evidence/g0/windows-export.log。")
    print("Windows真机验收：待执行。")


if __name__ == "__main__":
    try:
        package()
    except (ValueError, KeyError, OSError) as error:
        print("打包失败：" + str(error), file=sys.stderr)
        sys.exit(1)
