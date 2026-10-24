# 掘墓的EVE更新助手 2.2.0

开发者：WC飞机杯大王掘墓。Windows 10/11 x64 单文件便携应用。基于 2.1.0 修改，默认选择“快速”。

## 快速档重做

参考用户提供的 CloudflareST v2.2.5 的 TCP 排序后直接下载流程，保留 EVE 的真实域名验证。

1. 从随包 AWS CloudFront 网段随机抽样最多 2048 个 IP，加上六域名的当前 DNS、hosts 和用户补充节点。不先等待在线网段更新或下载资源清单。
2. 200 并发 TCP 测速，每个 IP 连接四次，按成功比例及平均延迟排序。
3. 用用户原来的 `https://resources.eveonline.com/d2/d218fb784d254413_60f784f44054fcc251a3afe5ac301c3f` 直接下载。最多比较 TCP 排名前 10 个候选，每个持续 10 秒；找到 3 个有效资源节点后结束主比较。零速度、错误页和失败结果不计入数量。预留时间不足时提前进入域名确认。
4. 取资源测速优胜节点，分别确认另外四个下载域名，每个持续 8 秒；失败则尝试其他已测资源节点或本域 DNS 候选。五个下载域名全部独立通过，才允许应用 hosts。旧界面 JSON 可选检查最长 3 秒。

快速总时限 180 秒，可以提前完成。它不先执行独立 HTTPS HEAD 广筛、跨地区短下载初筛、强制当前节点基线和多轮校准。每次实际下载仍保留真实域名、SNI、证书和文件类型验证；持续测试另有 2 秒预热和小请求检查。

快速侧重尽快找到可用高速节点，不保证找到全球或本轮候选中的绝对最快节点。网段抽样与网络会变化，因此每次结果可不同；若结果不足或速度不理想，切换标准档扩大搜索。标准仍最多 8 分钟，精校准不限时；两档保留原有流程。

## 使用

解压运行 EXE，无需另装 .NET。打开后默认“快速”，点“开始测速”。完成后从托盘完全退出 EVE 启动器，点“应用并检查”，允许管理员权限，再重新打开启动器更新。测速和诊断本身不写系统 hosts。

六个受支持域名：resources.eveonline.com、binaries.eveonline.com、resources-fallback.eveonline.com、binaries-fallback.eveonline.com、launcher.ccpgames.com，以及可选 launcher.eveonline.com。不能将一个域名的成功结果复制为另一个域名的验证结果。

网络速度仍按压缩文件实际字节统计；“解压读取”是 gzip 解压后的读数，用于对照原程序，不代表网络吞吐。快测沿用已知资源文件，标准和精校准会尝试获取当前版本文件。

保留下载诊断、启动器自身 IPv4/IPv6 连接记录、VPN 域名导出、hosts 备份与恢复。直连测速前关闭全局代理/TUN；代理路线在 VPN 客户端中另行验证。不会自动清理账号、SharedCache 或游戏设置。

## 验证与构建

发行包附回归检查和本机快速扫描摘要。回归覆盖快速流程跳过预筛、3 个有效节点后停止、零速度不计数、2048 抽样上限及 DNS 保留、各域名独立下载、失败重试与应用阻止、用户停止，以及原有标准/精校准、计速、日志、连接表、备份恢复功能。未为验证而修改系统 hosts，实际更新须应用节点并重开启动器后确认。

需要 Windows 和 .NET 8 SDK。从源码目录执行 `build.ps1`，或：

```powershell
dotnet publish EveUpdateHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist
```

验证入口：`--self-test 报告绝对路径`、`--probe-mode 报告绝对路径 Quick|Standard|Precise`、`--diagnose 报告绝对路径`。这些入口不修改系统 hosts。

历史版本 README 供追溯；当前行为以本文为准。参考项目：[CloudflareSpeedTest](https://github.com/XIU2/CloudflareSpeedTest)、[LLC BABEL](https://github.com/LocalizeLimbusCompany/LLC_BABEL)。保留原图标与作者署名，非 CCP 官方软件。
