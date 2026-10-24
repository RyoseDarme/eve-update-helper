# 掘墓的EVE更新助手 2.1.0

开发者：WC飞机杯大王掘墓。Windows 10/11 x64 单文件便携应用，保留原图标、署名和 CloudFront 节点筛选流程。基于提供的 2.0.0 源码修改。

## 本次修复

本机 EVE 启动器 1.17.2 的内置配置优先使用 `binaries-fallback.eveonline.com` 和 `resources-fallback.eveonline.com`，失败后才尝试不含 `-fallback` 的地址。1.4.0 和 2.0.0 仅优选四个旧域名，可能出现“测速及 hosts 检查通过，启动器仍然使用慢节点”。本机连接与日志已复现该遗漏。

2.1.0 处理六个域名：

| 用途 | 域名 |
| --- | --- |
| 新版优先二进制下载 | binaries-fallback.eveonline.com |
| 新版优先资源下载 | resources-fallback.eveonline.com |
| 兼容二进制下载 | binaries.eveonline.com |
| 兼容资源下载 | resources.eveonline.com |
| 启动器自身更新 | launcher.ccpgames.com |
| 旧界面版本 JSON，可选 | launcher.eveonline.com |

五个文件下载域名各自通过 HTTPS、持续下载和小请求验证后才可应用，不能把同组域名的测速成功直接复制给另一个域名。文件路径可共享，TLS SNI、证书验证和 HTTP Host 始终使用被测域名。原四域名的 hosts 会在新应用中更新，同时补齐两个优先域名。

已有 hosts 和补充节点会进入同组域名的候选池并重新验证，避免普通域名已找到的有效 IP 在 `-fallback` 测试中被漏掉。元数据读取同时尝试二进制域名的普通和优先地址，先成功者返回，取消其他请求，避免慢节点耗尽全部清单时间。资源清单失败时仍须实际验证备用文件。

## 使用

1. 解压后运行 `掘墓的EVE更新助手.exe`，无需另装 .NET。测速为直连，请先关闭全局代理/TUN，以便测量计划使用的直连线路。
2. 可先点“下载诊断”，查看是否存在旧规则遗漏、近期补丁速度以及启动器自身连接。
3. 选择“快速”或“标准”，开始测速。快速最多 5 分钟，标准最多 8 分钟；增加两个下载域名后相应延长预算。精校准不限时，可能消耗大量流量，可随时停止。
4. 测速通过后，从系统托盘完全退出 EVE 启动器，再点“应用并检查”。此时请求管理员权限，依次复核、备份、写入、刷新 DNS、检查连接。
5. 重新打开启动器更新。仍慢时，在启动器正在下载期间运行“下载诊断”，再“导出日志”。恢复按钮撤销最近一次应用前的 hosts，且检查后续其他程序的修改。

应用前无系统 hosts 写入。本工具不自动关闭启动器，不清空 SharedCache、账号或游戏设置。旧 TCP 连接不会因 DNS 刷新而断开，所以必须重开启动器。

## 下载诊断与 VPN

诊断读取 Windows TCP 连接表，分别记录启动器进程持有的 IPv4、IPv6 远端 IP/端口，并与已设置的 EVE hosts 地址作比较。TCP 表无法单独确定 HTTPS 的域名和完整 URL；同进程的登录连接也会显示。启动器未运行时会明确提示。

助手自身另外发起正常 DNS 解析的直连 HTTPS 请求，显示实际远端 IP。此结果只证明助手测试连接是否命中 hosts，不会再把它描述成启动器已命中。日志只提取下载域名、无查询参数的文件路径、补丁版本和有限速度记录，不导出原始认证/账户日志。历史速度记录会标明“可能已结束”。

窗口底部“导出 VPN 域名”可导出六域名的 Clash/Mihomo 规则示例。替换示例中的代理组名，放到 DIRECT/MATCH 等兜底规则前。启动器的下载器可能直接建立连接，单开系统 HTTP 代理未必接管它，可用 TUN/VPN 的域名或进程分流，并检查 VPN 客户端实际连接。助手不自动修改代理或 VPN 设置。

## 验证范围

109 项回归检查通过，覆盖旧功能及六域名独立验证、遗漏/失败结果禁止应用、当前清单刷新两个别名、慢元数据竞争取消、保留关联 hosts 候选、脱敏下载日志、IPv4/IPv6 TCP 表解析、真实 Windows TCP API 的本机回环连接、备份与恢复。检查只写工作区报告和临时测试文件。

发行包附本机联网验证摘要。它验证下载请求和节点选择，不等于已完成应用 hosts 后的 EVE 更新；本次没有为验证而修改系统 hosts。节点受时段、线路和文件影响，无法保证持续最快。历史 2.0.0 和 1.4.0 文档仅供追溯，当前行为以本文为准。

## 构建

安装 .NET 8 SDK，在此目录运行 `build.ps1`，或：

```powershell
dotnet publish EveUpdateHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist
```

自检：`dist\EveUpdateHelper.exe --self-test C:\某目录\自检报告.txt`。
联网诊断：`--diagnose 报告绝对路径`；快速扫描：`--probe-mode 报告绝对路径 Quick`。这些入口均不修改系统 hosts。

参考项目与来源：

- https://github.com/XIU2/CloudflareSpeedTest
- https://github.com/LocalizeLimbusCompany/LLC_BABEL
- https://ip-ranges.amazonaws.com/ip-ranges.json
- 本机已安装启动器的 `resources/app.asar`、近期启动器日志及 TCP/DNS 实测。

非 CCP 官方软件。未改动上述第三方项目。
