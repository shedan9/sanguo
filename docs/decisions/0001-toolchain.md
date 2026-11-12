# ADR-0001：G0候选工具链与验证边界

日期：2026-11-12。状态：候选配置已用于本地试验，待Windows独立设备验证后正式接受。`global.json`与包锁文件固定本次可复现配置，不表示SG-006已通过。

## 本次配置

| 项目 | 实测配置 |
| --- | --- |
| 开发机 | macOS 27.0、Apple M1 Pro、arm64 |
| SDK | .NET SDK 10.0.404，MSBuild 18.9.15 |
| 运行时 | Microsoft.NETCore.App 10.0.15 |
| 引擎 | Godot 4.7.2 stable .NET，`ed1daf0bf` |
| 目标框架 | 核心和Godot客户端均显式使用`net10.0` |
| 渲染 | Compatibility；实际窗口日志为OpenGL 4.1 Metal |
| 测试 | Microsoft.NET.Test.Sdk 17.14.1、xUnit 2.9.3、VS runner 3.1.4 |

SDK和编辑器置于项目忽略的`.tools/`，NuGet缓存置于`.nuget/`，脚本不更改全局shell。可改用已安装的同版SDK及`SANGUO_GODOT`指向的编辑器。普通终端可运行脚本；Codex沙箱中MSBuild/测试的通信管道受限，需通过权限流程在沙箱外验证。Godot启动包括无头模式一律按工作区规则走沙箱外流程。

Godot导入时会给未显式声明目标框架的项目插入`net8.0`，因此不能仅依赖根`Directory.Build.props`；`client/Sanguo.Client.csproj`显式声明`net10.0`后构建和运行均通过。

Godot .NET导出还要求`client/Sanguo.Client.sln`，不能仅提供根目录的`.slnx`。客户端传统解决方案已补齐；根`.slnx`继续作为不依赖Godot的检查入口。Godot可能在C#导出失败时返回0，因此构建脚本额外核对导出日志及所需程序集和运行时文件。

普通构建使用`packages.lock.json`，带目标平台的导出使用`packages.win-x64.lock.json`。恢复命令的`RuntimeIdentifiers`与发布命令的`RuntimeIdentifier`都参与锁文件路径选择，避免导出改写通用锁文件、导致下一次`--locked-mode`恢复失败。

## 下载来源与摘要

[微软.NET 10下载页](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)、[官方版本与文件摘要清单](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)、[Godot 4.7.2发布页](https://godotengine.org/download/archive/4.7.2-stable/)、[Godot官方SHA-512清单](https://github.com/godotengine/godot-builds/releases/download/4.7.2-stable/SHA512-SUMS.txt)。

已将下载文件的SHA-512与官方清单逐字比对：

```text
dotnet-sdk-10.0.404-osx-arm64.tar.gz
1c9a13f625d0c08f08ef5a53597bababe3d2473527e98db2199210940c69dd5fc599a39a4d7c30ebf297ca2334a81b2f64b9bf05ff99585de9af4a5d85215e96

Godot_v4.7.2-stable_mono_macos.universal.zip
0862c53d7158c7a67f745e2e46f90b68cf5343cbe8b95d6d4333c469e42ca104af9c121d1746a50e5d221a99d09d82ef7016495f8e0d09255842884ed0502795

Godot_v4.7.2-stable_mono_export_templates.tpz
bb5c41d72370ed743660361f6228006f808ab04ca33abdc545d740b044f3fe057f32ae8cb7873a1bc86ddcd82ae683b9f6dfdfe4179852f2c0f1acde2ff6bd5a
```

## G0证据状态

| 验证 | 状态 |
| --- | --- |
| 候选SDK构建纯C#库、自动测试 | 通过：25项测试，含固定随机向量与100种子各决策点恢复 |
| Godot加载同一库，执行命令并展示结果 | 通过：客户端零警告编译，实际窗口画面已检查 |
| Godot内JSON文件保存恢复 | 通过：`--smoke`输出一致状态哈希 |
| Windows x64导出包 | 通过交叉导出与文件结构检查：PE x64、项目程序集及.NET 10.0.15自包含运行时齐备；不等于目标机运行通过 |
| 独立Windows 11 x64运行且无需开发SDK | 待验证：当前没有该设备运行证据，不能用macOS导出代替 |

## 设计约束

G0场景仅抽取技术牌，不应用未冻结的OL技能，不产生战斗或战役奖励。工程、内容与存档的规则ID为`probe.draw.v1`，不会冒用正式`rules.sgs-online.v1`。

存档使用版本1信封及UTF-8 JSON载荷SHA-256，哈希不包含自身；每份快照包含版本、内容摘要、当前请求、随机状态与已接受命令。只在稳定决策点保存。文件在同目录创建、刷新、验证后替换，保留上一有效备份；这不承诺断电后的目录元数据持久性，也未替代Windows文件系统实测。

正式工具链接受之前，不启动OL人物与大规模业务内容批量生产。
