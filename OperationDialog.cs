using System.Diagnostics;

namespace EveUpdateHelper;

internal sealed record OperationProgress(int Percent, string Stage, string Detail);

internal sealed class OperationDialog : Form
{
    readonly Label heading = new() { AutoSize = false, Dock = DockStyle.Top, Height = 50, Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold) };
    readonly Label detail = new() { Dock = DockStyle.Top, Height = 96, Font = new Font("Microsoft YaHei UI", 10), Padding = new Padding(0, 8, 0, 0) };
    readonly PhaseProgress bar = new() { Dock = DockStyle.Top, Height = 12 };
    readonly Label elapsed = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 10, 0, 0), ForeColor = Color.FromArgb(87, 100, 122) };
    readonly Button close = new() { Text = "完成", Width = 110, Height = 38, Enabled = false, BackColor = Color.FromArgb(41, 106, 230), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    readonly Stopwatch watch = Stopwatch.StartNew();
    readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool finished;
    internal OperationDialog(bool restore)
    {
        Text = restore ? "恢复设置" : "应用更新节点";
        ClientSize = new Size(560, 310); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false;
        BackColor = Color.FromArgb(244, 247, 252); Font = new Font("Microsoft YaHei UI", 10);
        var layout = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28) };
        Controls.Add(layout);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(close);
        layout.Controls.AddRange([buttons, elapsed, bar, detail, heading]);
        heading.ForeColor = Color.FromArgb(27, 41, 68);
        heading.Text = restore ? "正在恢复设置" : "正在应用更新节点";
        detail.Text = "正在准备，请在系统提示中允许管理员权限。\r\n接下来会复核节点、备份 hosts，并检查是否生效。";
        timer.Tick += (_, _) => { elapsed.Text = $"已用时 {watch.Elapsed.TotalSeconds:F0} 秒 · 按操作步骤显示进度"; bar.Pulse(); };
        timer.Start();
        close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (!finished) e.Cancel = true; };
        FormClosed += (_, _) => { timer.Dispose(); completion.TrySetResult(); };
    }
    internal void UpdateProgress(OperationProgress progress)
    {
        heading.Text = progress.Stage; detail.Text = progress.Detail;
        bar.Percent = Math.Clamp(progress.Percent, 0, 100);
        elapsed.Text = $"已用时 {watch.Elapsed.TotalSeconds:F0} 秒 · 按操作步骤显示进度";
    }
    internal void Finish(bool success, string message)
    {
        finished = true; watch.Stop(); timer.Stop();
        heading.Text = success ? "设置已完成" : "操作未完成";
        heading.ForeColor = success ? Color.FromArgb(30, 132, 94) : Color.FromArgb(166, 66, 66);
        detail.Text = message;
        if (success) bar.Percent = 100;
        elapsed.Text = $"用时 {watch.Elapsed.TotalSeconds:F1} 秒 · 详细结果已记录到主窗口日志";
        close.Enabled = true; close.Text = success ? "完成" : "关闭";
    }
    internal Task WaitForCloseAsync() => completion.Task;

    sealed class PhaseProgress : Control
    {
        int? percent;
        int pulse;
        internal int? Percent { set { percent = value; Invalidate(); } }
        internal PhaseProgress() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        internal void Pulse() { pulse = (pulse + 24) % Math.Max(1, Width); if (percent == null) Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            using var background = new SolidBrush(Color.FromArgb(218, 227, 242));
            using var foreground = new SolidBrush(Color.FromArgb(41, 106, 230));
            e.Graphics.FillRectangle(background, ClientRectangle);
            if (percent.HasValue) e.Graphics.FillRectangle(foreground, 0, 0, Width * percent.Value / 100, Height);
            else e.Graphics.FillRectangle(foreground, pulse, 0, Math.Min(90, Width - pulse), Height);
        }
    }
}
