# 掘墓的EVE更新助手 2.0.0

WC飞机杯大王掘墓参考 XIU2/CloudflareSpeedTest **v2.3.5** 和 LLC BABEL 的流程独立开发。没有直接复制 Go 源码，也没有在发行包中捆绑或调用原工具。Windows 10/11 x64 单文件便携应用，内含 .NET 运行时，保留原圆角图标和作者署名。

## 重做范围

1.4版主要依赖DNS返回的少量地址，无法覆盖同一CDN的其他可用边缘。2.0版引入 AWS 官方 CloudFront 网段，解决候选来源不足。候选数量和各阶段完成数量明确分开，不把TCP成功当作文件下载成功。

当前官方快照包含211条CloudFront IPv4网段，按每个/24随机抽样一个公网IP得到约16552个候选。每次测速先尝试在线更新，8秒内不可用则使用内嵌快照。只取AWS清单中service=CLOUDFRONT的项目，排除AMAZON及其他服务；不沿用旧Cloudflare ip.txt。网段抽样不等于穷举所有IP，不能保证全球绝对最快。

## 新流程

1. 并行读取实际DNS、用户补充节点和官方CloudFront清单，保留当前系统节点。
2. 对整个抽样池做200并发TCP443初探，每个IP先连接一次，超时1秒。全部初探结束后，再对入围地址按档位复核4或6次，避免一开始对上万个不通IP重复等待。纯连接探测及时释放socket。
3. 入围HTTP池兼顾DNS、低延迟及不同网段。快速/标准/精校准分别选最多96/192/512个公共候选，另保留各域名自身DNS节点。对于三个文件域名分别执行HTTPS HEAD，保留真实域名、SNI和证书验证，检查状态及文件类型，提取真实X-Amz-Cf-Pop地区。每次预检及时关闭连接。
4. 按实际边缘地区轮流探索下载，避免前排全部来自同一地区。短下载只作候选排序，不能直接应用。
5. 持续下载首先对当前系统或用户补充节点作基线，再比较扩大池中的优胜节点。使用完整文件GET，预热2秒后按实际字节与完整时窗计算速率；文件提前结束则继续请求，不允许短样本冒充长测。
6. 三个文件域名均有持续下载及小请求验证通过的结果后才可应用。旧版界面域名独立验证JSON，失败不阻止前三者应用。

HTTP失败、证书错误或文字错误页直接排除，并记录原因。新版表格只显示深入验证候选，没有创建上万行TCP结果；完整探测量、HTTPS完成量和下载通过量见日志及JSON报告。表格更新合并成批，避免每个探测结果都排序重绘。

## 档位

| 档位 | 深入比较 | 总时间 |
| --- | --- | --- |
| 快速 | 当前节点基线 + 不同地区代表，每域名最多3个持续10秒样本 | 最多180秒，通常2–3分钟 |
| 标准 | 最多45秒跨地区下载初筛，按初测排序，再逐个持续20秒验证 | 最多300秒，通常3–5分钟 |
| 精校准 | 三次DNS合并，扩大HTTP池，初筛最多180秒；每域名选最多24个地区代表与实测优胜节点持续20秒，再复测前三名 | 不设总时限，可停止 |

精校准前三名各有三轮单连接测量（20/30/30秒，第三轮比较随机大区间），取中位值和最低轮评估稳定性；另外各做两轮三路并行20秒。三条连接响应就绪后共用计时窗口，排除共同2秒预热，按总字节/共同时间计算；两轮均通过才显示较低的总速。推荐分数结合60%单连接稳定性与40%三路保守速度，三路验证不足时保留单连接结论并注明。

以上是优选启发式流程。精校准也没有对全部1.65万地址逐个长时间下载，实际效果受线路、时段、节点和文件影响。可能需要数十分钟并消耗数十至上百GB流量。快/标准档截止时停止网络工作，只保留已完成的验证；不足以开始一次完整测试时提前结束。

## 速度口径

主列为压缩文件实际收到字节的网络单连接速度，单位MiB/s，排除2秒预热，不包含TLS和HTTP包头。快速档另外显示gzip解压读取速度，用于理解原工具的读取口径；它不等于宽带吞吐。标准和精校准直接读取压缩文件，避免解压CPU开销干扰。

最新版原工具仍使用Go自动gzip解压及滑动估算；本工具使用完整时窗平均，不能要求两个数字逐次一致。已对相同IP 52.222.244.51、相同EVE备用资源文件作顺序对照：官方v2.3.5显示16.19；助手网络11.41MiB/s、解压读取19.14MiB/s。两次测量有时间差，不能据此认定谁的网络恒定更快。

每次持续测试的样本上限为max(2GiB,(持续秒数+2)×128MiB)，足够覆盖70MB/s级别30秒测量；达到上限但时间不足仍不可应用。三路样本总上限为相应单连接上限的三倍。

## 使用与生效

解压运行EXE，关闭全局代理/TUN后测速。底部“补充测速节点”接受每行“域名 公网IPv4”，仅本次会话有效，仍需真实下载验证。未通过、未测试和短测结果不可直接写hosts。

独立处理 resources.eveonline.com、binaries.eveonline.com、launcher.ccpgames.com，以及旧版 launcher.eveonline.com。域名清单不是所有版本的完整网络范围，额外下载域名会通过本机近期启动器日志提示，不擅自改登录和游戏服务器地址。

应用前完全退出游戏和启动器，包括托盘进程。点击应用后立即显示进度窗口，请在系统提示中允许管理员权限。复核节点、备份、写入、DNS刷新和实际连接检查均有阶段进度；进度表示步骤而非剩余时间估计。完成后重开启动器。若仍慢，检查生效、导出诊断，并核对启动器真实请求日志。

hosts保留其他域名、编码、注释及空行。写前独立备份；恢复时检查是否存在其他程序的后续修改，避免覆盖。工具不删除SharedCache、账号或游戏设置。DNS刷新不会关闭旧TCP连接。

## 验证

93项离线检查通过，包括官方网段抽样与去重、异常网段大小限制、在线清单不可用回退、排除非CloudFront服务、HTTP成功不能冒充下载成功、跨地区候选、基线保留、70+MiB/s越过旧2GB上限、gzip口径区分、同窗三路计时及hosts安全恢复。

完整模拟精校准流程也已通过：DNS只有一个低延迟慢节点，官方网段有DNS未返回的快节点；最终能推荐后者，且三轮单连接、两轮三路及覆盖计数完整。该项为确定性模拟测试，不冒充真实网络整轮精校准。

首轮网络验证：快速171.1秒，TCP16607个不同IP，HTTPS288/288，214次通过；标准282.2秒，TCP16600，HTTPS576/576，465次通过。两档均为三个文件下载域名找到有效推荐。最终增加基线后的标准档实测285.9秒，TCP16615，HTTPS576/576、474次通过，6个文件节点持续验证通过；资源推荐节点来自官方网段，持续19.47MiB/s。报告为standard-final-2.0.0.json。

本机未安装EVE，未执行真实启动器更新；未完成全部真实候选的整轮精校准，也没有在电信/联通/移动各地逐一端到端测试。测试只写工作区报告和临时hosts测试文件，没有修改系统hosts，也未更改用户代理、启动器和原脚本。

## 来源与构建

- https://github.com/XIU2/CloudflareSpeedTest/releases/tag/v2.3.5
- https://github.com/XIU2/CloudflareSpeedTest/blob/v2.3.5/task/ip.go
- https://github.com/XIU2/CloudflareSpeedTest/blob/v2.3.5/task/tcping.go
- https://github.com/XIU2/CloudflareSpeedTest/blob/v2.3.5/task/httping.go
- https://github.com/XIU2/CloudflareSpeedTest/blob/v2.3.5/task/download.go
- https://github.com/XIU2/CloudflareSpeedTest/discussions/304
- https://ip-ranges.amazonaws.com/ip-ranges.json
- https://docs.aws.amazon.com/vpc/latest/userguide/aws-ip-ranges.html
- https://github.com/LocalizeLimbusCompany/LLC_BABEL
- https://pkg.go.dev/net/http#Transport

官方对照发行包SHA256：67d06a0c68b7fd6998d5e6abea1dbf850cac2c19c5d8c5980aa32fc7aba1ff5f，已与GitHub资产digest核对。该包不随助手发行。

.NET8构建：`dotnet publish EveUpdateHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist`。

内部验证入口：`--self-test 报告绝对路径`、`--probe-mode 报告绝对路径 Quick|Standard|Precise`、`--probe-one 报告绝对路径 域名 IP 秒数 true|false`、`--diagnose 报告绝对路径`。除用户点击应用/恢复所启动的专用管理员操作外，验证入口不写系统hosts。
