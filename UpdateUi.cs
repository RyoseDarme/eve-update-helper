using System.Diagnostics;

namespace EveUpdateHelper;

internal sealed partial class MainForm
{
    readonly LinkLabel updateLink = new()
    {
        Text = "检查更新", AutoSize = true, Margin = new Padding(24, 20, 0, 0),
        Font = new Font("Microsoft YaHei UI", 10), LinkColor = Color.FromArgb(41, 106, 230)
    };
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    readonly UpdatePreferences updatePreferences = UpdatePreferences.Load();
    CancellationTokenSource? updateCancellation;
    ReleaseInfo? availableRelease;
    bool checkingUpdates;

    void InitializeUpdates()
    {
        updateLink.LinkClicked += async (_, _) =>
        {
            if (availableRelease != null) ShowUpdateDialog(availableRelease, "");
            else await CheckForUpdates(true);
        };
        Shown += async (_, _) =>
        {
            await Task.Delay(1500);
            if (IsDisposed || Disposing) return;
            updateTimer.Start();
            await CheckForUpdates(false);
        };
        updateTimer.Tick += async (_, _) => await CheckForUpdates(false);
        FormClosed += (_, _) => { updateCancellation?.Cancel(); updateTimer.Dispose(); };
    }

    async Task CheckForUpdates(bool manual)
    {
        if (checkingUpdates || IsDisposed || Disposing) return;
        if (busy)
        {
            if (manual) MessageBox.Show(this, "请在测速、诊断或应用结束后检查更新。", "检查更新");
            return;
        }
        if (!manual && !updatePreferences.AutomaticChecks) return;
        checkingUpdates = true;
        updateLink.Text = "正在检查更新…";
        using var checkCancellation = new CancellationTokenSource();
        updateCancellation = checkCancellation;
        try
        {
            var result = await UpdateChecker.CheckAsync(AppInfo.Repository, AppInfo.Version, checkCancellation.Token);
            if (IsDisposed || Disposing || busy) return;
            availableRelease = result.Release;
            updateLink.Text = result.Release == null ? "检查更新" : $"发现新版本 v{result.Release.Version} · 查看";
            updateLink.LinkColor = result.Release == null ? Color.FromArgb(41, 106, 230) : Color.FromArgb(184, 87, 18);
            if (manual) ShowUpdateDialog(result.Release, result.Message);
        }
        catch (OperationCanceledException) when (checkCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (manual && !IsDisposed && !Disposing && !busy)
                ShowUpdateDialog(null, "暂时无法检查更新，请稍后重试。\r\n" +
                    (ex is OperationCanceledException ? "连接 GitHub 超时。" : NetworkScanner.ErrorText(ex)));
        }
        finally
        {
            checkingUpdates = false;
            updateCancellation = null;
            if (!IsDisposed && !Disposing)
                updateLink.Text = availableRelease == null ? "检查更新" : $"发现新版本 v{availableRelease.Version} · 查看";
        }
    }

    void ShowUpdateDialog(ReleaseInfo? release, string message)
    {
        using var dialog = new Form
        {
            Text = "软件更新", ClientSize = new Size(670, 450), Font = Font,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            FormBorderStyle = FormBorderStyle.FixedDialog
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        dialog.Controls.Add(layout);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = release == null ? message :
            $"发现新版本 v{release.Version}（当前 v{AppInfo.Version}）\r\n下载新版后解压运行即可。", AutoSize = false }, 0, 0);
        layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, Text = release == null ?
                "打开助手后自动检查正式版本，发现新版时在主窗口提醒。\r\n联网失败不影响测速；开始测速时会暂停版本检查。" :
                string.IsNullOrWhiteSpace(release.Notes) ? "本次发布未填写更新说明。" : release.Notes }, 0, 1);
        var automatic = new CheckBox { Text = "启动后自动检查更新（运行期间每 6 小时检查一次）", AutoSize = true,
            Checked = updatePreferences.AutomaticChecks, Margin = new Padding(0, 10, 0, 0) };
        automatic.CheckedChanged += (_, _) =>
        {
            bool previous = updatePreferences.AutomaticChecks;
            if (previous == automatic.Checked) return;
            updatePreferences.AutomaticChecks = automatic.Checked;
            try { updatePreferences.Save(); }
            catch
            {
                updatePreferences.AutomaticChecks = previous;
                automatic.Checked = previous;
                MessageBox.Show(dialog, "无法保存设置，请检查当前用户文件夹权限。", "软件更新");
            }
        };
        layout.Controls.Add(automatic, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, Width = 90, Height = 34 };
        buttons.Controls.Add(close);
        var download = new Button { Text = release == null ? "打开发布页" : "前往下载新版", Width = 140, Height = 34 };
        download.Enabled = !string.IsNullOrWhiteSpace(AppInfo.Repository);
        download.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(release?.PageUrl ?? $"https://github.com/{UpdateChecker.ValidateRepository(AppInfo.Repository)}/releases")
                    { UseShellExecute = true });
            }
            catch { MessageBox.Show(dialog, "无法打开浏览器，请稍后重试。", "软件更新"); }
        };
        buttons.Controls.Add(download);
        layout.Controls.Add(buttons, 0, 3);
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }
}
