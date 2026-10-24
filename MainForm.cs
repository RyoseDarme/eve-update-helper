using System.Diagnostics;
using System.Text.Json;

namespace EveUpdateHelper;

internal sealed partial class MainForm : Form
{
    readonly Button scan = Button("开始测速", Color.FromArgb(41, 106, 230));
    readonly Button apply = Button("应用并检查", Color.FromArgb(30, 132, 94));
    readonly Button restore = Button("恢复原设置", Color.FromArgb(72, 83, 103));
    readonly Button cancel = Button("停止", Color.FromArgb(72, 83, 103));
    readonly Button diagnose = Button("下载诊断", Color.FromArgb(72, 83, 103));
    readonly ComboBox duration = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 116, Margin = new Padding(0, 8, 10, 0) };
    readonly DataGridView grid = new();
    readonly TextBox logs = new();
    readonly Label status = new();
    readonly Label summary = new();
    readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer rowTimer = new() { Interval = 150 };
    readonly System.Collections.Concurrent.ConcurrentDictionary<(string Domain, string IP), NodeResult> pendingRows = new();
    readonly Dictionary<(string Domain, string IP), DataGridViewRow> indexedRows = new();
    readonly Stopwatch scanWatch = new();
    int totalSeconds = 120;
    string lastProgress = "准备就绪";
    CancellationTokenSource? cancellation;
    ScanResult? latest;
    readonly Dictionary<string, string[]> suppliedCandidates = new();
    bool busy;

    static Button Button(string text, Color color) => new()
    {
        Text = text, AutoSize = false, Size = new Size(140, 42), BackColor = color,
        ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        Margin = new Padding(0, 0, 12, 0), Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold)
    };

    internal MainForm()
    {
        Text = "掘墓的EVE更新助手 " + AppInfo.Version;
        using (var executableIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!))
            if (executableIcon != null) Icon = (Icon)executableIcon.Clone();
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(1180, 820);
        MinimumSize = new Size(1160, 800);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(244, 247, 252);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
        foreach (var size in new float[] { 60, 54, 58, 90, 0, 30, 140, 40 })
            layout.RowStyles.Add(new RowStyle(size == 0 ? SizeType.Percent : SizeType.Absolute, size == 0 ? 100 : size));
        Controls.Add(layout);
        var title = new Label { Text = "掘墓的EVE更新助手", Font = new Font("Microsoft YaHei UI", 24, FontStyle.Bold), AutoSize = true, ForeColor = Color.FromArgb(27, 41, 68) };
        var titleBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        titleBar.Controls.Add(title);
        titleBar.Controls.Add(updateLink);
        layout.Controls.Add(titleBar, 0, 0);
        InitializeUpdates();
        var intro = new Label { Text = "电信 · 联通 · 移动，按当前网络实时选节点\r\n六域名完整覆盖，包含新版优先使用的两个 -fallback 地址；下载诊断可查看启动器自身连接。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(87, 100, 122) };
        layout.Controls.Add(intro, 0, 1);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        toolbar.Controls.AddRange([scan, apply, restore, cancel, diagnose]);
        toolbar.Controls.Add(new Label { Text = "测速档位", AutoSize = true, Margin = new Padding(0, 12, 8, 0) });
        duration.Items.AddRange(["快速", "标准", "精校准"]); duration.SelectedIndex = 0;
        var modeTip = new ToolTip();
        modeTip.SetToolTip(duration, "默认快速：TCP 排序后直接测速，找到3个有效资源节点即确认其他域名，最多3分钟。标准扩大搜索，最多8分钟；精校准不限时。");
        duration.DrawMode = DrawMode.Normal;
        duration.ForeColor = Color.FromArgb(27, 41, 68);
        toolbar.Controls.Add(duration);
        var export = new Button { Text = "导出日志", Width = 90, Height = 34, Margin = new Padding(0, 4, 0, 0) };
        export.Click += (_, _) =>
        {
            using var save = new SaveFileDialog { Filter = "文本日志|*.txt|完整诊断报告|*.json", FileName = "EVE更新助手诊断.txt" };
            if (save.ShowDialog(this) == DialogResult.OK)
                File.WriteAllText(save.FileName, save.FilterIndex == 2
                    ? JsonSerializer.Serialize(new { AppVersion = AppInfo.Version, SavedAt = DateTime.Now, Log = logs.Lines, Results = latest }, Program.JsonOptions)
                    : logs.Text);
        };
        toolbar.Controls.Add(export);
        apply.Enabled = false; cancel.Enabled = false;
        layout.Controls.Add(toolbar, 0, 2);
        summary.Text = "默认快速：参考原程序，TCP 排序后直接下载，找到 3 个有效资源节点即确认其他域名。\r\n主测速 10 秒，其余下载域名独立确认 8 秒；快速最多 3 分钟。标准扩大搜索，精校准保留长测。\r\n包含两个 -fallback 域名；零速度和失败结果不可应用。测速完成后完全退出启动器，再应用节点。";
        summary.Font = new Font("Microsoft YaHei UI", 9);
        summary.Dock = DockStyle.Fill;
        summary.Padding = new Padding(12, 10, 12, 0);
        summary.BackColor = Color.FromArgb(230, 237, 249);
        summary.ForeColor = Color.FromArgb(35, 67, 117);
        layout.Controls.Add(summary, 0, 3);
        grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
        grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ScrollBars = ScrollBars.Both;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 10);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold);
        grid.ColumnHeadersHeight = 44; grid.RowTemplate.Height = 38;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(246, 248, 253);
        foreach (var (name, width) in new[] { ("下载域名", 225), ("TCP 延迟 (ms)", 115), ("网络单连接 (MiB/s)", 160), ("节点 IP", 150), ("初测 (MiB/s)", 130), ("HTTPS (ms)", 110), ("小请求 (MiB/s)", 145), ("已测 (秒)", 95), ("三路总速 (MiB/s)", 160), ("校准轮数", 95), ("解压读取 (MiB/s)", 160), ("地区", 110), ("状态 / 原因", 360) })
        {
            var column = new DataGridViewTextBoxColumn { HeaderText = name, Width = width, MinimumWidth = width, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (grid.Columns.Count < 3) column.Frozen = true;
            if (grid.Columns.Count is 1 or 2 or 4 or 5 or 6 or 7) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns.Add(column);
        }
        layout.Controls.Add(grid, 0, 4);
        status.Text = "准备就绪"; status.Dock = DockStyle.Fill; status.Padding = new Padding(0, 6, 0, 0);
        layout.Controls.Add(status, 0, 5);
        logs.Multiline = true; logs.ReadOnly = true; logs.ScrollBars = ScrollBars.Vertical;
        logs.Dock = DockStyle.Fill; logs.BackColor = Color.White; logs.BorderStyle = BorderStyle.FixedSingle;
        logs.Font = new Font("Microsoft YaHei UI", 9);
        layout.Controls.Add(logs, 0, 6);
        var credits = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        credits.Controls.Add(new Label
        {
            Text = "WC飞机杯大王掘墓 · 参考 CloudflareSpeedTest v2.3.5 与 LLC BABEL 开发",
            AutoSize = true, ForeColor = Color.FromArgb(87, 100, 122), Font = new Font("Microsoft YaHei UI", 9), Margin = new Padding(0, 2, 18, 0)
        });
        var about = new LinkLabel { Text = "关于与项目来源", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9), Margin = new Padding(0, 2, 0, 0) };
        about.LinkClicked += (_, _) => ShowAbout();
        credits.Controls.Add(about);
        var extra = new LinkLabel { Text = "补充测速节点", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9), Margin = new Padding(18, 2, 0, 0) };
        extra.LinkClicked += (_, _) => { if (!busy) EditCandidates(); };
        credits.Controls.Add(extra);
        var vpn = new LinkLabel { Text = "导出 VPN 域名", AutoSize = true, Font = new Font("Microsoft YaHei UI", 9), Margin = new Padding(18, 2, 0, 0) };
        vpn.LinkClicked += (_, _) =>
        {
            using var save = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = "EVE更新_VPN分流域名.txt" };
            if (save.ShowDialog(this) == DialogResult.OK) File.WriteAllText(save.FileName, EveDomains.ProxyRules, new System.Text.UTF8Encoding(true));
        };
        credits.Controls.Add(vpn);
        layout.Controls.Add(credits, 0, 7);
        Log("请先关闭游戏加速器的全局代理 / TUN，再测直连节点。应用后需要重启 EVE 启动器。");
        scan.Click += async (_, _) => await Scan();
        cancel.Click += (_, _) => { cancellation?.Cancel(); status.Text = "正在停止…"; };
        apply.Click += async (_, _) => await ChangeHosts(false);
        restore.Click += async (_, _) => await ChangeHosts(true);
        diagnose.Click += async (_, _) => await Diagnose();
        scanTimer.Tick += (_, _) => status.Text = (totalSeconds == 0 ? $"已用时 {scanWatch.Elapsed.TotalSeconds:F0} 秒" : $"剩余 {Math.Max(0, totalSeconds - scanWatch.Elapsed.TotalSeconds):F0} 秒") + " · " + lastProgress;
        rowTimer.Tick += (_, _) => FlushRows(); rowTimer.Start();
        FormClosed += (_, _) => { scanTimer.Dispose(); rowTimer.Dispose(); };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            if (cancellation != null) { cancellation.Cancel(); status.Text = "正在停止，请稍后关闭窗口。"; }
            else status.Text = "正在写入或恢复，请等待操作结束再关闭。";
        };
    }

    void ShowAbout()
    {
        using var dialog = new Form
        {
            Text = "关于 · 掘墓的EVE更新助手", ClientSize = new Size(680, 340),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, Font = Font, BackColor = BackColor
        };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24) };
        dialog.Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "掘墓的EVE更新助手 " + AppInfo.Version, AutoSize = true, Font = new Font(Font.FontFamily, 18, FontStyle.Bold), Margin = new Padding(0, 0, 0, 14) });
        panel.Controls.Add(new Label
        {
            Text = "开发者：WC飞机杯大王掘墓\r\n\r\n参考 CloudflareSpeedTest 的 CDN 下载测速方案，\r\n以及 LLC BABEL 的多 DNS、逐域名验证与 hosts 备份恢复方案。\r\n本应用独立实现，没有直接复制上述项目的代码。\r\n面向 EVE 国际服下载优化，非 CCP 官方软件。",
            AutoSize = true, Margin = new Padding(0, 0, 0, 14)
        });
        foreach (var (name, url) in new[] {
            ("CloudflareSpeedTest · XIU2", "https://github.com/XIU2/CloudflareSpeedTest"),
            ("LLC BABEL · LocalizeLimbusCompany", "https://github.com/LocalizeLimbusCompany/LLC_BABEL") })
        {
            var link = new LinkLabel { Text = name, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
            link.LinkClicked += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "无法打开项目链接"); }
            };
            panel.Controls.Add(link);
        }
        dialog.ShowDialog(this);
    }

    void UI(Action action)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) BeginInvoke(action); else action();
    }
    void Log(string message) => UI(() => { logs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n"); lastProgress = message; status.Text = message; });
    void Row(NodeResult result) => pendingRows[(result.Domain, result.IP)] = result;
    void FlushRows()
    {
        var update = pendingRows.Take(128).ToArray();
        if (update.Length == 0) return;
        grid.SuspendLayout();
        DataGridViewRow? active = null;
        foreach (var pair in update)
            if (pendingRows.TryRemove(pair.Key, out var result))
            {
                ApplyRow(result);
                if (result.Detail.StartsWith("测速中")) active = indexedRows[pair.Key];
            }
        grid.Sort(new NodeRowComparer());
        if (active != null) grid.FirstDisplayedScrollingRowIndex = active.Index;
        grid.ResumeLayout();
    }
    void ApplyRow(NodeResult result)
    {
        var key = (result.Domain, result.IP);
        if (!indexedRows.TryGetValue(key, out var existing))
        { existing = grid.Rows[grid.Rows.Add()]; indexedRows[key] = existing; }
        existing.Tag = result;
        bool measuring = result.Detail.StartsWith("测速中");
        existing.SetValues(result.Domain, result.TcpLatencyMs > 0 ? result.TcpLatencyMs.ToString("F0") : "未连接",
            result.SpeedMBps > 0 ? result.SpeedMBps.ToString("F2") : measuring ? "预热" : result.Domain == NetworkScanner.Domains[2] ? "不测下载" : "未测",
            result.IP, result.InitialSpeedMBps > 0 ? result.InitialSpeedMBps.ToString("F2") : "未测",
            result.LatencyMs > 0 ? result.LatencyMs.ToString("F0") : "未测",
            result.SmallFileMBps > 0 ? result.SmallFileMBps.ToString("F2") : "未测",
            result.MeasuredSeconds > 0 ? result.MeasuredSeconds.ToString("F0") : "0",
            result.ParallelSpeedMBps > 0 ? result.ParallelSpeedMBps.ToString("F2") : "未测",
            result.CalibrationRounds > 0 ? result.CalibrationRounds.ToString() : "—",
            result.DecodedSpeedMBps > 0 ? result.DecodedSpeedMBps.ToString("F2") : "—", result.Region, result.Detail);
        bool unmeasured = result.Detail.Contains("未测") || result.Detail.StartsWith("仅 HTTPS") || result.Detail.Contains("尚未") || result.Detail.Contains("尚需");
        existing.DefaultCellStyle.ForeColor = measuring ? Color.FromArgb(41, 106, 230) : unmeasured ? Color.FromArgb(110, 118, 132) : result.Success ? Color.FromArgb(33, 63, 59) : Color.FromArgb(166, 66, 66);
        foreach (DataGridViewCell cell in existing.Cells) cell.ToolTipText = result.Detail;
    }
    sealed class NodeRowComparer : System.Collections.IComparer
    {
        static int Priority(NodeResult node) => node.Detail.StartsWith("测速中") ? 0 : node.Success && node.SpeedMBps > 0 ? 1 :
            node.Success && node.Domain == NetworkScanner.Domains[2] ? 2 : node.InitialSpeedMBps > 0 ? 3 : node.TcpReceived > 0 ? 4 : 5;
        public int Compare(object? a, object? b)
        {
            var left = (NodeResult)((DataGridViewRow)a!).Tag!;
            var right = (NodeResult)((DataGridViewRow)b!).Tag!;
            int priority = Priority(left).CompareTo(Priority(right));
            if (priority != 0) return priority;
            int domain = Array.IndexOf(NetworkScanner.Domains, left.Domain).CompareTo(Array.IndexOf(NetworkScanner.Domains, right.Domain));
            if (domain != 0) return domain;
            return (right.SpeedMBps > 0 ? right.SpeedMBps : right.InitialSpeedMBps).CompareTo(left.SpeedMBps > 0 ? left.SpeedMBps : left.InitialSpeedMBps);
        }
    }
    internal void PreviewReport(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var scan = JsonSerializer.Deserialize<ScanResult>(document.RootElement.GetProperty("results").GetRawText());
        if (scan == null) return;
        foreach (var node in scan.Nodes) Row(node);
        while (!pendingRows.IsEmpty) FlushRows();
        summary.Text = "界面校验：以下为已保存的真实测试报告，不会修改 hosts。\r\n延迟和持续速度位于固定的前三列；右侧信息可横向滚动。\r\n报告测量总用时：" + scan.ElapsedSeconds.ToString("F1") + " 秒";
    }
    void Busy(bool value, bool scanning = false)
    {
        busy = value;
        if (value) updateCancellation?.Cancel();
        scan.Enabled = !value; restore.Enabled = !value;
        cancel.Enabled = value && scanning;
        apply.Enabled = !value && NetworkScanner.CanApply(latest);
        diagnose.Enabled = !value; duration.Enabled = !value;
    }

    async Task Scan()
    {
        latest = null; pendingRows.Clear(); indexedRows.Clear(); grid.Rows.Clear(); Busy(true, true);
        summary.BackColor = Color.FromArgb(230, 237, 249);
        cancellation = new CancellationTokenSource();
        var selectedMode = (ScanMode)duration.SelectedIndex;
        totalSeconds = ScanProfile.For(selectedMode).TotalSeconds;
        scanWatch.Restart(); scanTimer.Start();
        summary.Text = "正在查找和测试节点。下载测试逐个进行，请等待；随时可点停止。";
        try
        {
            var token = cancellation.Token;
            latest = await Task.Run(() => new NetworkScanner(Log, Row, mode: selectedMode, additionalCandidates: suppliedCandidates).RunAsync(token), token);
            while (!pendingRows.IsEmpty) FlushRows();
            summary.Text = NetworkScanner.CanApply(latest)
                ? "测速完成，尚未应用：请完全退出 EVE 启动器，然后点击「应用并检查」。\r\n" +
                    string.Join("  |  ", NetworkScanner.Domains.Where(latest.Best.ContainsKey).Select(d => $"{d.Replace(".eveonline.com", "")} → {latest.Best[d].IP}")) +
                    "\r\n五个下载域名均已独立通过持续下载；旧界面域名可选。"
                : "五个下载域名尚未全部通过持续验证（含两个 -fallback），不能应用。查看日志中的具体原因。";
            if (latest.Coverage is { } coverage)
                summary.Text += selectedMode == ScanMode.Quick
                    ? $"\r\n快速直接下载：TCP 已测 {coverage.TcpProbed} 个 IP；持续验证通过 {coverage.SustainedPassed} 个节点。"
                    : $"\r\nTCP 已测 {coverage.TcpProbed} 个 IP；HTTPS 完成 {coverage.HttpsCompleted}/{coverage.HttpsPlanned}；持续验证通过 {coverage.SustainedPassed} 个节点。";
            foreach (var r in grid.Rows.Cast<DataGridViewRow>())
                if (r.Tag is NodeResult node && latest.Best.TryGetValue(node.Domain, out var winner) && node.IP == winner.IP)
                    r.DefaultCellStyle.BackColor = Color.FromArgb(222, 243, 229);
            if (grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = 0;
            Log(NetworkScanner.CanApply(latest) ? $"用时 {latest.ElapsedSeconds:F1} 秒。绿色行是已验证推荐节点；尚未改动 hosts。请点击「应用并检查」。" :
                $"用时 {latest.ElapsedSeconds:F1} 秒；有效验证未齐全。可选择较长模式重测，hosts 未修改。");
        }
        catch (OperationCanceledException) { summary.Text = "测速已停止，hosts 未修改。"; Log("已停止测速。"); }
        catch (Exception ex) { summary.Text = "测速失败，hosts 未修改。"; Log(NetworkScanner.ErrorText(ex)); }
        finally { scanTimer.Stop(); scanWatch.Stop(); cancellation.Dispose(); cancellation = null; Busy(false); }
    }

    async Task ChangeHosts(bool restoreOriginal)
    {
        if (!restoreOriginal && !NetworkScanner.CanApply(latest)) return;
        if (!restoreOriginal && ConnectionDiagnostics.LauncherProcesses().Count > 0)
        {
            Log("EVE 启动器仍在运行。请在系统托盘完全退出启动器，再点击「应用并检查」。");
            MessageBox.Show(this, "请在系统托盘完全退出 EVE 启动器，再应用节点。\r\n关闭窗口可能只是最小化到托盘，旧连接仍可能继续使用。", "先退出启动器");
            return;
        }
        if (restoreOriginal && !File.Exists(HostsManager.SystemHosts + ".evehelper.json"))
        {
            Log("还没有本工具的恢复记录；尚未应用过节点。"); return;
        }
        Busy(true);
        using var progressDialog = new OperationDialog(restoreOriginal);
        progressDialog.Show(this);
        await Task.Delay(50);
        var temp = Path.Combine(Path.GetTempPath(), "EveUpdateHelper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var planPath = Path.Combine(temp, "plan.json");
        try
        {
            File.WriteAllText(planPath, JsonSerializer.Serialize(new ApplyPlan(latest?.Best.Values.ToList() ?? [], latest?.TestedAt ?? DateTime.Now), Program.JsonOptions));
            Log(restoreOriginal ? "申请管理员权限以恢复 hosts…" : "申请管理员权限，复核节点后应用 hosts…");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add(restoreOriginal ? "--restore" : "--apply");
            start.ArgumentList.Add(planPath);
            using var process = Process.Start(start)!;
            while (!process.HasExited)
            {
                await Task.Delay(250);
                var progressPath = planPath + ".progress.json";
                try
                {
                    if (File.Exists(progressPath))
                    {
                        var progress = JsonSerializer.Deserialize<OperationProgress>(File.ReadAllText(progressPath));
                        if (progress != null) progressDialog.UpdateProgress(progress);
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            if (!File.Exists(planPath + ".result.json")) throw new IOException("操作未完成，没有收到结果。请检查管理员权限或安全软件提示。");
            var result = JsonSerializer.Deserialize<OperationResult>(File.ReadAllText(planPath + ".result.json"))!;
            Log(result.Message);
            if (result.Success)
            {
                summary.BackColor = Color.FromArgb(222, 243, 229);
                summary.Text = restoreOriginal ? "已恢复最近一次应用前的设置。请重新打开 EVE 启动器。" :
                    "验证通过的更新域名已写入 hosts，已尝试刷新 DNS。实际连接检查结果见日志。\r\n现在重新打开 EVE 启动器更新；不要只看测速结果判断是否已生效。\r\n若仍慢，点击「检查生效」并「导出日志」，同时查看 EVE 启动器的下载日志。";
                if (restoreOriginal) latest = null;
            }
            var dialogText = result.Success ? result.Message.Split('\n')[0] + "\r\n实际连接检查结果请查看日志。请重新打开 EVE 启动器。" : result.Message;
            progressDialog.Finish(result.Success, dialogText);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log("你取消了管理员权限申请，hosts 未修改。"); progressDialog.Finish(false, "管理员权限申请已取消，hosts 未修改。"); }
        catch (Exception ex) { Log("操作失败：" + ex.Message); progressDialog.Finish(false, ex.Message); }
        finally
        {
            foreach (var file in Directory.GetFiles(temp)) File.Delete(file);
            Directory.Delete(temp);
            Busy(false);
        }
        await progressDialog.WaitForCloseAsync();
    }

    void EditCandidates()
    {
        using var dialog = new Form { Text = "补充已知测速节点", ClientSize = new Size(620, 390), StartPosition = FormStartPosition.CenterParent,
            Font = Font, BackColor = BackColor, MinimizeBox = false, MaximizeBox = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        dialog.Controls.Add(layout);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "可添加以前测得的高速节点，每行写域名和 IP，例如：\r\nbinaries-fallback.eveonline.com 3.170.229.35\r\n接受六个已支持域名的公网 IPv4；各域名仍须独立通过真实 HTTPS 下载验证。" }, 0, 0);
        var input = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Text = string.Join(Environment.NewLine, suppliedCandidates.SelectMany(p => p.Value.Select(ip => p.Key + " " + ip))) };
        layout.Controls.Add(input, 0, 1);
        var save = new Button { Text = "保存候选", Width = 120, Height = 36, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        layout.Controls.Add(save, 0, 2);
        save.Click += (_, _) =>
        {
            var parsed = new Dictionary<string, List<string>>();
            foreach (var line in input.Lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                var values = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length != 2 || !NetworkScanner.Domains.Contains(values[0]) || !NetworkScanner.PublicIPv4(values[1]))
                { dialog.Text = "格式无效，请填写支持的域名和公网 IPv4"; return; }
                if (!parsed.ContainsKey(values[0])) parsed[values[0]] = [];
                parsed[values[0]].Add(values[1]);
            }
            suppliedCandidates.Clear();
            foreach (var pair in parsed) suppliedCandidates[pair.Key] = pair.Value.Distinct().ToArray();
            Log($"已补充 {suppliedCandidates.Values.Sum(a => a.Length)} 个候选；本次会话有效，下次开始测速时一并验证。" );
            dialog.Close();
        };
        dialog.ShowDialog(this);
    }

    async Task Diagnose()
    {
        Busy(true, true);
        cancellation = new CancellationTokenSource();
        try
        {
            var token = cancellation.Token;
            await Task.Run(() => ConnectionDiagnostics.InspectAsync(latest?.Best, Log, token), token);
            Log("生效检查完成。可点击「导出日志」保存报告。");
        }
        catch (OperationCanceledException) { Log("诊断已停止。"); }
        catch (Exception ex) { Log("诊断失败：" + ex.Message); }
        finally { cancellation.Dispose(); cancellation = null; Busy(false); }
    }
}
