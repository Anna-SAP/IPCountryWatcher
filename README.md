# IP 国旗监视器（Windows 11）

[![Windows build](https://github.com/Anna-SAP/IPCountryWatcher/actions/workflows/build.yml/badge.svg)](https://github.com/Anna-SAP/IPCountryWatcher/actions/workflows/build.yml)

[下载安装程序](https://github.com/Anna-SAP/IPCountryWatcher/releases/latest/download/IPCountryWatcher-Setup.exe) · [下载便携版](https://github.com/Anna-SAP/IPCountryWatcher/releases/latest/download/IPCountryWatcher-portable.zip) · [最新发布](https://github.com/Anna-SAP/IPCountryWatcher/releases/latest)



原生 Windows 系统托盘应用。自动获取本程序的公网出口 IP，识别国家 / 地区，并把国旗显示在任务栏右下角。

## 直接使用

双击 **bin/IPCountryWatcher.exe**。无需管理员权限，无需安装 Python 或 .NET SDK。发布时请保留同目录的 .exe.config、说明文件和四个 Probe / ProbeHost 组件。
如果未看见图标，展开右下角的隐藏图标区域；可以把图标拖到任务栏，或在 Windows 的任务栏设置中启用其显示。应用不能强制绕过 Windows 的托盘折叠设置。

- 悬停：查看国家代码和当前公网 IP。
- 右键：查看详细状态、手动刷新、复制 IP、设置检查间隔、切换系统代理、开关变化通知、设置开机启动、退出。
- 双击：立即触发刷新。
- 首次启动或新 IP 尚未识别：灰色地球。手动刷新、网卡地址事件或短暂查询失败时，最多保留上次确认后的 2 分钟国旗；悬停与菜单明确标注“待确认 / 上次”，不把旧 IP 当作本次查询结果，也不能复制为当前 IP。
- Windows 报告网络不可用、切换系统代理选项或睡眠恢复时立即清除旧国旗。确认 IP 已变但国家未知、或超过缓冲期时也显示灰色地球。
- 开机启动默认关闭，开启后仅写入当前用户的 Run 注册表项。移动程序后请重新关闭、开启此选项。

## 监听机制与时效

监听网卡地址变化、网络可用性变化和睡眠恢复；合并约 200 毫秒内的事件后发起检查；启动和手动刷新立即发起检查。
同时默认每轮检查完成后等待 5 秒再检查，可改为 10 / 30 / 60 秒。没有本机网卡事件的公网 NAT / 代理出口变化由轮询发现。
“即时”指事件触发后尽快查询；公网出口没有通用的本机推送事件，因此无法保证零延迟。实际发现时间是等待间隔加请求耗时。

请求在后台运行，单个请求及每组并发查询均有 4 秒超时上限。主服务超过 200 毫秒未返回就启动备用服务，主服务失败则立即启动备用服务；首个通过验证的结果直接更新 UI，并取消其余请求。公网 IP 查询首次失败后约 5 秒重试，连续失败依次退避至 10 / 20 / 40 / 60 秒，不受正常轮询间隔影响；手动刷新和网络事件会重置退避并提前重试。
同一时刻只运行一轮；切网会取消旧请求，版本标记阻止旧结果覆盖新状态。退出时取消请求并释放托盘图标、GDI 句柄和事件订阅。

## 出口与代理

默认跟随 Windows 系统代理；取消该项后使用本机路由直接发出请求（VPN/TUN 仍可能影响路由）。
每轮重新获取代理配置。浏览器插件、应用专用 SOCKS 代理及按域名分流可能产生不同出口，显示结果仅代表本程序查询服务看到的出口。
优先查询公网 IPv4（ipv4.icanhazip.com，慢于 200 毫秒或失败时并发请求同一主机的 Cloudflare /cdn-cgi/trace，采用最先验证成功的结果）；两者失败或达到本组超时后才使用 ifconfig.me 尝试 IPv4/IPv6。
查询服务均按 TCP 连接的来源地址返回 IP，不采信 X-Forwarded-For。Zscaler 等解密 HTTPS 的代理会在请求中插入该头并写入代理前的本机公网 IP，ipify 等采信该头的服务因此报告代理前的地址，而不是网站实际看到的出口。公司代理还可能按目标网站选择不同出口：两个 IPv4 查询共用同一 Cloudflare 主机，经过同一出口，结果代表 Cloudflare 上的网站看到的出口；备用的 ifconfig.me 位于 Google Cloud，可能看到另一个出口。
托盘顶部仅展示本程序的一个当前出口；进程监控窗口独立列出目标进程的双栈结果。切换到 IPv6 备用查询源也可能表现为 IP 变化。

## 进程 IP 监控（VPN 分流 / 代理绕过）

托盘右键 → **监控应用设置**，选择目标 EXE 并勾选“启用探针”；再打开 **进程 IP 监控** 查看独立结果。预置爱奇艺、腾讯视频、优酷视频、QQ音乐、哔哩哔哩、微信 WeChat、ChatGPT、Claude、Grok Bot 的可编辑条目，可增加、删除、改名或停用。预置名称不等于已适配或已实测，默认不启用任何探针。

- 使用“从运行进程选择”绑定真实 EXE，或浏览文件。“匹配已运行的预置应用”只填入检测到的路径，不自动启用探针。应用升级改变安装路径后需重新选择；ChatGPT 的匹配会排除 Codex 的同名可执行文件。
- 按 **完整 EXE 路径 + PID + 进程创建时间** 区分实例。优先探测该 EXE 中拥有已建立非回环 TCP 连接的进程；无此连接时选择最早启动的一个实例。窗口分别列出选中的 PID、IPv4/IPv6、公网 IP、国家、探针模式、时间和失败状态。
- 默认每个结果完成后约 30 秒进入下一轮，可设 15 / 30 / 60 秒；最多同时执行两个探针，排队、超时和国家查询会延长实际间隔。关闭窗口后仍持续监控，退出托盘程序或在设置中停用可停止后续探测。
- 手动刷新、网络地址变化、网络可用性变化及睡眠恢复会使旧结果失效。超过 90 秒的结果标为过期；进程退出、PID 复用、查询失败时不会拿本程序 IP 或旧国家填充。
- **直连（含 VPN）**：进程内 WinHTTP 不使用 HTTP 系统代理，但连接仍受 PrivadoVPN 等产品针对该进程的分流策略影响。**系统代理**：使用 Windows 自动代理配置，不读取目标应用内部的 SOCKS/HTTP 代理设置。

### 探测原理与边界

仅查询 Windows TCP 表只能得到本地端点、对端和所属 PID；本地网卡地址、服务器 IP 或 VPN 路由本身无法证明 NAT / 代理后的公网出口。此功能由配套的 x86/x64 DLL 在目标进程中请求固定 HTTPS 地址，并将结果通过该进程的内存回传给监视器。国家查询由监视器针对这个明确 IP 进行，复用缓存、超时与限流处理，不以监视器自己的出口替代。

IPv4 使用 ipv4.icanhazip.com，失败后尝试同一主机的 /cdn-cgi/trace；IPv6 独立使用 ipv6.icanhazip.com，均按 TCP 连接的来源地址返回 IP。界面保留实际获胜查询源。IPv6 不可用不会抹掉有效 IPv4，反之亦然。结果代表**这个 PID 在所选探针模式下访问测试服务时的出口**，不承诺目标应用访问所有网站、UDP/QUIC、内置代理或其它联网子进程都使用相同出口。共享 WebView、Python 等宿主需自行选择实际联网 EXE；不会自动把其它程序的共享宿主归到某个应用。

进程内加载可能被 DRM、反作弊、沙箱、受保护进程或权限隔离拦截，也可能影响兼容性。探针只允许同一 Windows 用户和会话，不申请调试特权、不提权、不绕过应用保护；访问拒绝会显示原因。当前支持 x64 Windows 上的 x86/x64 进程。加载超时等不可恢复错误在该进程本次生命周期内停止自动重试。

正常完成后卸载 DLL。网络事件、停用或退出时，已在目标内执行的请求会继续完成清理，但结果不再接收；不会强制终止目标线程。如果目标线程长时间无响应，为避免释放仍在使用的内存，DLL/小块参数内存可能保留至目标应用退出；重启目标应用可恢复探测。不要在探针执行期间升级组件，建议先退出监视器并等待在途请求结束。

### PrivadoVPN 实机验收

1. 启动目标应用，在监控设置中绑定它的完整 EXE 并启用，选择“直连（含 VPN）”。
2. 记录该应用测试服务的 IP；在 PrivadoVPN SmartRoute 中对**同一个联网 EXE**设置通过隧道或绕过。
3. 等待 VPN 策略生效，再点“立即重新探测”，比较同一地址族及同一测试服务的 IP。必要时按 VPN 的要求重启目标应用。
4. 切换规则后若返回相同 IP，只能说明该次测试看到了相同出口；检查规则选中的 EXE、子进程和测试站点规则，不从网卡名称猜测结论。
5. 无法连接、IPv6 不可用、权限不足、应用保护等均应显示明确状态。九个预置应用并不保证均允许加载探针，应逐一验证。


## 国家与国旗

对**已获取的明确 IP**调用 ipwho.is 查询国家；慢于 200 毫秒或失败时启动 ipapi.co 备用查询。两个服务均校验响应 IP 与所查询 IP 一致，避免使用另一服务自己的出口地址误配国旗。
成功的国家结果按 IP 缓存 24 小时；仅在新 IP、缓存过期或失败后的重试窗口到来时查询。缓存过期后若国家服务暂时失败，仅对本轮重新确认的同一 IP 沿用不足 7 天的国家缓存，并标注“缓存待更新”、约 5 秒后重试；失败不会延长缓存寿命，也不会把旧 IP 的国家套给新 IP。
普通国家查询错误按服务冷却 5 秒；国家尚未识别时，即使检查间隔设为 30 / 60 秒，也会在本轮完成后约 5 秒再次尝试。HTTP 429 按服务分别尊重 Retry-After（未给出时等待 24 小时），一个服务限流不会阻止备用服务，其间仍持续监控 IP。
252 张国家 / 地区 / 组织旗帜作为 PNG 内嵌在 EXE 中，运行时无需下载；服务返回未知代码时显示代码占位。
地理信息来自第三方数据库，VPN、代理、地址分配变化会影响准确性；国家标识不等于设备物理位置。

## 网络与本地数据

运行程序会向 icanhazip（Cloudflare）/ ifconfig.me 发起 HTTPS 请求以查询本机公网 IP，向 ipwho.is / ipapi.co 提交该 IP 查询国家；这些服务会获知请求来源的公网 IP。
所有查询使用 HTTPS；不发送姓名、设备名、本机私有 IP 或浏览历史。未添加遥测和 IP 历史文件。
设置（含所选应用名称、EXE 路径、启用状态和探针模式）保存至 %LOCALAPPDATA%/IPCountryWatcher/settings.json。进程列表和测量结果只保留在内存，未发送给查询服务；公网查询只发送必要的 IP 请求。国家缓存仅在进程内。
服务需要互联网访问；未配置付费密钥，免费服务限流或不可用时程序会显示状态并自动重试。

## 构建与测试

在此目录运行 Windows PowerShell 或 PowerShell：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
~~~

build.ps1 使用 Windows 随附的 .NET Framework C# 编译器，以及 Visual Studio Build Tools 的“使用 C++ 的桌面开发”（MSVC x86/x64 和 Windows 10/11 SDK）。构建不下载 NuGet；运行已发布应用不需要安装这些开发工具。
也提供 IPCountryWatcher.csproj，供安装了 .NET Framework 4.8 开发工具的 Visual Studio 使用。
测试使用内存中的模拟 HTTP 服务，不向公网接口发送请求；覆盖 IP / JSON 验证、缓存、快速失败恢复、独立限流、延迟启动备用源、忽略取消的慢请求、并发结果过期保护、IPv4 优先与 IPv6 回退、国旗资源和真实 WinForms 托盘显示延迟。
新增离线测试还会在本项目自建的 x86/x64 测试进程中验证 DLL 加载、PID 回传、卸载、再次加载和错误身份拒绝；不会向第三方应用加载 DLL，也不会发起公网请求。窗口预览输出至 test-results/process-monitor-preview.png 和 process-settings-preview.png。
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

1. 向 **main** 推送 src、native、assets、tests、installer、build.ps1、项目文件、应用配置或工作流改动时自动运行；仅修改 README 等说明文档不触发。也可从 Actions 手动运行。
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

便携包和安装包均包含 x86/x64 的探针 DLL 与加载器；离线测试用的 ProbeFixture 不随应用分发。
构建结果：dist/IPCountryWatcher-Setup.exe、dist/IPCountryWatcher-portable.zip、dist/SHA256SUMS.txt。
仓库提交全部源代码、测试与国旗资源；bin、obj、dist、test-results 为自动生成目录，不提交到 Git。

## 官方资料

- Windows 网络地址事件：https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkchange
- icanhazip 说明：https://major.io/icanhazip-com-faq/
- Cloudflare /cdn-cgi/trace：https://developers.cloudflare.com/fundamentals/reference/cdn-cgi-endpoint/
- ifconfig.me 接口：https://ifconfig.me/
- 国家查询字段与限流：https://ipwhois.io/documentation
- 备用国家查询：https://ipapi.co/api/
- 国旗资源：https://flagpedia.net/download/api

第三方来源见 THIRD-PARTY-NOTICES.md。

进程探测相关官方资料：
- [Windows TCP 连接表与 PID](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)
- [WFP 应用层身份与网络策略](https://learn.microsoft.com/en-us/windows/win32/fwp/application-layer-enforcement--ale-)
- [跨进程线程及兼容性影响](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createremotethread)
- [PrivadoVPN 应用分流](https://privadovpn.com/split-tunneling/)
