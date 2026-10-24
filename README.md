# 掘墓的EVE更新助手 2.3.0

开发者：WC飞机杯大王掘墓。Windows 10/11 x64，单文件便携运行，无需另外安装 .NET。

[下载最新正式版](https://github.com/RyoseDarme/eve-update-helper/releases/latest) · [项目源码](https://github.com/RyoseDarme/eve-update-helper)

## 新版本提醒

本版起支持检查官方 GitHub Releases。启动后后台检查一次，保持运行时每六小时再检查；发现新版本会在主窗口标题旁显示提示。点击提示可查看更新说明并打开本项目的下载页。也可点击“检查更新”手动查询，或在更新窗口取消自动检查。

只提醒正式版本，草稿和预发布不会提示。网络超时、GitHub 访问失败或限流不影响测速；开始测速、诊断或应用时会取消未完成的版本请求。版本检查使用系统网络代理设置，不携带 GitHub 账号、访问令牌、测速结果或启动器日志。自动检查设置保存在当前 Windows 用户的 LocalAppData/EveUpdateHelper/update-settings.json。

2.2.0 及更早版本没有检查功能，需要手动换成本版一次。更新提醒从已集成本功能的版本开始生效。点击下载只打开浏览器，由用户解压运行新版；不会后台替换程序。

官方仓库由 AppInfo.cs 中的 Repository 指定，版本由 AppInfo.Version 与项目文件 Version 指定。

## EVE 下载测速

默认“快速”，参考 CloudflareST 的 TCP 排序后直接下载方式：随包 CloudFront 网段随机抽样最多2048个 IP，加入 DNS、hosts 和补充节点；200并发 TCP，各连接4次。

主资源使用原有测速文件，最多比较 TCP 排名前10个候选，每个持续10秒，找到3个有效节点后进入其他下载域名确认。其他四域名各持续8秒，包括 binaries-fallback.eveonline.com 和 resources-fallback.eveonline.com。五个下载域名均独立通过才能应用。零速度、错误页、失败或未完成结果不计入可应用节点。

快速最多180秒；标准扩大搜索、最多480秒；精校准不限时。下载速度以网络实际字节计算，“解压读取”仅供对照。快速不等待在线网段更新、资源清单或 HEAD 广筛，标准和精校准保留原有流程。

## 使用

1. 解压运行 EXE，关闭全局代理/TUN 后点击“开始测速”。
2. 测速结束，从托盘完全退出 EVE 启动器，点击“应用并检查”。
3. 允许管理员权限，完成后重新打开启动器更新。可用“恢复原设置”还原助手修改。

测速和诊断本身不修改 hosts；应用有备份。保留启动器连接诊断、VPN 域名导出、补充节点与日志导出。不自动清理账号、SharedCache 或游戏设置。

## 构建与验证

需要 Windows 和 .NET 8 SDK，在源码目录运行 build.ps1。发布为 win-x64 的 self-contained 单文件。验证命令：

```powershell
.\dist\EveUpdateHelper.exe --self-test C:\temp\self-test.txt
.\dist\EveUpdateHelper.exe --check-update C:\temp\update-check.json
```

也保留 --probe-mode、--probe-one 与 --diagnose。诊断入口不写系统 hosts。发行包附离线自检与验证说明。后续发版方法见“发布指南.md”。

历史 README 保留供追溯，当前行为以本文为准。参考项目：[CloudflareSpeedTest](https://github.com/XIU2/CloudflareSpeedTest)、[LLC BABEL](https://github.com/LocalizeLimbusCompany/LLC_BABEL)。保留原图标和作者署名，非 CCP 官方软件。
