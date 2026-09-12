# IP 国旗监视器（Windows 11）

[![Windows build](https://github.com/Anna-SAP/IPCountryWatcher/actions/workflows/build.yml/badge.svg)](https://github.com/Anna-SAP/IPCountryWatcher/actions/workflows/build.yml)

[下载安装程序](https://github.com/Anna-SAP/IPCountryWatcher/releases/latest/download/IPCountryWatcher-Setup.exe) · [下载便携版](https://github.com/Anna-SAP/IPCountryWatcher/releases/latest/download/IPCountryWatcher-portable.zip) · [最新发布](https://github.com/Anna-SAP/IPCountryWatcher/releases/latest)



原生 Windows 系统托盘应用。自动获取本程序的公网出口 IP，识别国家 / 地区，并把国旗显示在任务栏右下角。

## 直接使用

双击 **bin/IPCountryWatcher.exe**。无需管理员权限，无需安装 Python 或 .NET SDK。发布时请保留同目录的 .exe.config 和说明文件。
如果未看见图标，展开右下角的隐藏图标区域；可以把图标拖到任务栏，或在 Windows 的任务栏设置中启用其显示。应用不能强制绕过 Windows 的托盘折叠设置。

- 悬停：查看国家代码和当前公网 IP。
- 右键：查看详细状态、手动刷新、复制 IP、设置检查间隔、切换系统代理、开关变化通知、设置开机启动、退出。
- 双击：立即触发刷新。
- 首次启动、切网、未连接或查询失败：灰色地球。新 IP 尚未识别时不会沿用旧国家的国旗。
- 开机启动默认关闭，开启后仅写入当前用户的 Run 注册表项。移动程序后请重新关闭、开启此选项。

## 监听机制与时效

监听网卡地址变化、网络可用性变化和睡眠恢复；合并约 200 毫秒内的事件后发起检查；启动和手动刷新立即发起检查。
同时默认每轮检查完成后等待 5 秒再检查，可改为 10 / 30 / 60 秒。没有本机网卡事件的公网 NAT / 代理出口变化由轮询发现。
“即时”指事件触发后尽快查询；公网出口没有通用的本机推送事件，因此无法保证零延迟。实际发现时间是等待间隔加请求耗时。

请求在后台运行，单个请求及每组并发查询均有 4 秒超时上限。主服务超过 200 毫秒未返回就启动备用服务，主服务失败则立即启动备用服务；首个通过验证的结果直接更新 UI，并取消其余请求。连续公网 IP 查询失败会逐步退避至 60 秒，手动刷新和网络事件可提前重试。
同一时刻只运行一轮；切网会取消旧请求，版本标记阻止旧结果覆盖新状态。退出时取消请求并释放托盘图标、GDI 句柄和事件订阅。

## 出口与代理

默认跟随 Windows 系统代理；取消该项后使用本机路由直接发出请求（VPN/TUN 仍可能影响路由）。
每轮重新获取代理配置。浏览器插件、应用专用 SOCKS 代理及按域名分流可能产生不同出口，显示结果仅代表本程序查询服务看到的出口。
优先查询公网 IPv4（api.ipify.org，慢于 200 毫秒或失败时并发启动 checkip.amazonaws.com，采用最先验证成功的结果）；两者失败或达到本组超时后才使用 api64.ipify.org 尝试 IPv4/IPv6。按域名分流时，这两个 IPv4 服务也可能看到不同出口。
仅展示一个当前出口，不同时列出所有网卡和双栈地址；切换到 IPv6 备用查询源也可能表现为 IP 变化。

## 国家与国旗

对**已获取的明确 IP**调用 ipwho.is 查询国家；慢于 200 毫秒或失败时启动 ipapi.co 备用查询。两个服务均校验响应 IP 与所查询 IP 一致，避免使用另一服务自己的出口地址误配国旗。
成功的国家结果按 IP 缓存 24 小时；仅在新 IP、缓存过期或失败后的重试窗口到来时查询。
普通国家查询错误按服务冷却 5 秒；国家尚未识别时，即使检查间隔设为 30 / 60 秒，也会在本轮完成后约 5 秒再次尝试。HTTP 429 按服务分别尊重 Retry-After（未给出时等待 24 小时），一个服务限流不会阻止备用服务，其间仍持续监控 IP。
252 张国家 / 地区 / 组织旗帜作为 PNG 内嵌在 EXE 中，运行时无需下载；服务返回未知代码时显示代码占位。
地理信息来自第三方数据库，VPN、代理、地址分配变化会影响准确性；国家标识不等于设备物理位置。

## 网络与本地数据

运行程序会向 ipify / AWS checkip 发起 HTTPS 请求以查询本机公网 IP，向 ipwho.is / ipapi.co 提交该 IP 查询国家；这些服务会获知请求来源的公网 IP。
所有查询使用 HTTPS；不发送姓名、设备名、本机私有 IP 或浏览历史。未添加遥测和 IP 历史文件。
设置保存至 %LOCALAPPDATA%/IPCountryWatcher/settings.json。国家缓存仅在进程内。
服务需要互联网访问；未配置付费密钥，免费服务限流或不可用时程序会显示状态并自动重试。

## 构建与测试

在此目录运行 Windows PowerShell 或 PowerShell：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
~~~

build.ps1 使用 Windows 随附的 .NET Framework C# 编译器，无 NuGet 下载、无 SDK 安装。
也提供 IPCountryWatcher.csproj，供安装了 .NET Framework 4.8 开发工具的 Visual Studio 使用。
测试使用内存中的模拟 HTTP 服务，不向公网接口发送请求；覆盖 IP / JSON 验证、缓存、快速失败恢复、独立限流、延迟启动备用源、忽略取消的慢请求、并发结果过期保护、IPv4 优先与 IPv6 回退、国旗资源和真实 WinForms 托盘显示延迟。
测试日志在 test-results/results.txt，测试时会短暂出现托盘图标并自动退出。

真实联网查询和 VPN / Wi-Fi 切换验收尚需在允许访问上述服务的环境中进行。验收时应确认：
1. 启动后悬停显示公网 IP，国旗与国家信息一致。
2. 切换 Wi-Fi、VPN 或代理出口后自动更新；同国不同 IP 也更新悬停信息。
3. 断网后进入灰色待确认状态，恢复网络后自动重新识别。
4. 退出后图标消失；重复启动只保留一个实例。


## 安装包与自动发布

优先从本仓库的 Releases 下载 **IPCountryWatcher-Setup.exe**。安装到当前用户目录，无需管理员权限；提供开始菜单快捷方式、可选桌面快捷方式和标准卸载入口。安装向导目前使用英文界面，程序菜单为中文。
安装程序不会默认启动应用；可在最后一步选择启动，或从开始菜单打开。升级前建议从托盘退出应用。
卸载会清理指向该安装目录的开机启动项，并保留用户设置。当前提供未签名构建，首次下载运行时 Windows 可能显示发布者验证提示。

工作流位于 [.github/workflows/build.yml](.github/workflows/build.yml)：

1. 向 **main** 推送 src、assets、tests、installer、build.ps1、项目文件、应用配置或工作流改动时自动运行；仅修改 README 等说明文档不触发。也可从 Actions 手动运行。
2. Windows Server 2022 runner 编译 C#，运行离线测试，并使用预装的 Inno Setup 6 生成安装包。
3. 在临时 runner 中静默安装，校验文件哈希、确认应用未自动启动，再静默卸载。此过程不发起公网 IP 查询。
4. 保存测试报告、Setup.exe、便携 ZIP 和 SHA256 校验文件。主分支成功构建后自动创建 GitHub Release 并标记为 Latest；PR 仅验证和保存工件，不发布。
5. 构建版本自动递增为 1.0.<workflow run number>；发布标签还包含 run ID 和重试次数，避免重跑覆盖旧产物。连续提交会取消同分支尚未完成的旧构建，以最新提交为准。

Actions 使用 GitHub 自动提供的 GITHUB_TOKEN，无需添加个人访问令牌。只有发布作业拥有 contents: write 权限；官方 Actions 固定到已核实的提交 SHA。
工件默认保留 30 天，测试报告保留 14 天；正式 Release 附件长期保留。
安装器本地构建需要 Inno Setup 6；普通 EXE / 便携包构建不需要。

~~~powershell
# 本地构建、离线测试、便携包（无 Inno Setup 依赖）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test -Package -Version 1.0.1

# 同时生成可安装的 Setup.exe（需本机已安装 Inno Setup 6）
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test -Installer -Version 1.0.1
~~~

构建结果：dist/IPCountryWatcher-Setup.exe、dist/IPCountryWatcher-portable.zip、dist/SHA256SUMS.txt。
仓库提交全部源代码、测试与国旗资源；bin、obj、dist、test-results 为自动生成目录，不提交到 Git。

## 官方资料

- Windows 网络地址事件：https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange
- ipify 接口：https://www.ipify.org/
- 国家查询字段与限流：https://ipwhois.io/documentation
- 备用国家查询：https://ipapi.co/api/
- 国旗资源：https://flagpedia.net/download/api

第三方来源见 THIRD-PARTY-NOTICES.md。
