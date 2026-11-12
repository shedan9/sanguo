# G0 Windows独立设备验收

本流程服务SG-002、SG-006。自动检查通过只是一份工程证据，不会自动接受工具链、冻结OL来源或关闭G0。当前没有Windows真机执行证据，PowerShell入口也仍待目标机实测。

## 1 准备与执行

开发机执行`./scripts/export-windows.sh`。macOS上的Codex必须在沙箱外运行此命令。脚本先执行规则、内容和打包器测试，再导出自包含Windows x64包；打包器检查PE机器类型、核心程序集、运行时配置与导出日志，将验收脚本、许可和逐文件SHA-256一并放入`build/sanguo-g0-windows-x64.zip`。

完整解压到独立Windows 11 x64设备。目标机不需要.NET开发SDK、Godot编辑器或Python。断网后，在包目录打开Windows PowerShell 5.1执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\verify-windows.ps1
```

执行策略参数只影响本次进程，不更改系统设置。每次客户端启动默认限时90秒，可用`-TimeoutSeconds 180`调整；超时只结束该次验收启动的进程。

## 2 自动检查及报告

1. 记录系统、构建号和CPU架构，要求Windows 11构建号范围和x64 CPU。通过PATH及常用.NET安装目录记录可发现的SDK；发现SDK时不能作为干净目标设备通过。
2. 核对每个文件大小和SHA-256，拒绝缺失、额外文件、重复路径、目录穿越和链接，并记录清单本身的摘要。清单用于检测意外改动，不是发行签名。
3. 启动`--smoke`，要求正常退出、无引擎错误且符合既有固定哈希。
4. 启动`--acceptance-write <绝对路径>`，种子42抽三张牌，保存至本轮专用目录后退出。
5. 在新进程执行`--acceptance-resume <同一目录>`，核对检查点、下一步结果、重复命令幂等和ID冲突拒绝；保存第四张牌后损坏专用测试档，验证备份回退、修复保留备份和再次续行一致。

日志、`acceptance-report.json`和`save/`试验档位于系统临时目录中唯一命名的`sanguo-g0-*`目录。不会操作交互场景的`user://g0-probe/session.json`。写入阶段要求空目录，恢复阶段先比对预期检查点，再进行破坏测试；每轮须使用新目录。

返回0表示自动检查通过且未发现SDK；返回1表示失败，原因见`failure`。即使返回0，`g0Accepted`仍为`false`，人工检查仍为`pending`。SDK清点不能证明设备上没有任意自定义安装；离线状态由执行者记录，脚本不自动推断。

## 3 人工验证与归档

在同一设备、离线环境启动`Sanguo.exe`，记录设备标识、系统、分辨率和缩放，保存窗口截图。检查中文、四个按钮、Tab焦点及回车/空格操作。抽三张保存、抽第四张后恢复应回到三张，退出重启仍可恢复。

归档原始压缩包与摘要、完整自动验收目录、人工记录及截图。不得只改报告中的待办状态来替代证据。工具链接受仍需审阅平台差异；SG-006也仍依赖规则及界面来源冻结等G0条件。

## 4 本次验证边界

新增5项C#验收测试和7项Python打包回归；打包测试使用合成PE和运行时文件，不能作为实际导出证据。客户端编译通过，并在macOS运行两个独立Godot进程，得到：

```text
G0_ACCEPTANCE_WRITE_OK 5093ff90b4912d79ceabf67252bfa07960ccaf2d017d0f0874d6dea6d598cd03
G0_ACCEPTANCE_RESUME_OK 2d7c2445abf4319c28a026185f92787080e40e8bc59be671e066f7f27ae0f432
```

哈希绑定当前内容、种子和命令ID。macOS结果仅证明共享验收代码和Godot参数接入可运行，不替代Windows PowerShell、文件系统、离线及窗口验收。
