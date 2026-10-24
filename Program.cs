using System.Text.Json;

namespace EveUpdateHelper;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            try
            {
                if (args[0] == "--self-test")
                {
                    File.WriteAllText(args[1], SelfTests.Run());
                    return;
                }
                if (args[0] == "--check-update")
                {
                    string current = args.Length > 2 ? args[2] : AppInfo.Version;
                    var result = UpdateChecker.CheckAsync(AppInfo.Repository, current, CancellationToken.None).GetAwaiter().GetResult();
                    File.WriteAllText(args[1], JsonSerializer.Serialize(new { Repository = AppInfo.Repository, CurrentVersion = current, Result = result }, JsonOptions));
                    return;
                }
                if (args[0] is "--probe" or "--probe-fallback" or "--probe-mode")
                {
                    var log = new List<string>();
                    var logGate = new object();
                    File.WriteAllText(args[1] + ".log", "");
                    Func<Uri, int, CancellationToken, Task<string>>? fault = args[0] == "--probe-fallback"
                        ? (_, _, _) => Task.FromException<string>(new IOException("模拟清单全部不可达，验证备用测速流程")) : null;
                    ScanMode? mode = args[0] == "--probe-mode" ? Enum.Parse<ScanMode>(args[2], true) : null;
                    int budgetSeconds = mode.HasValue ? 120 : args.Length > 2 ? int.Parse(args[2]) : 120;
                    var results = new NetworkScanner(s => { lock (logGate) { log.Add(s); File.AppendAllText(args[1] + ".log", s + Environment.NewLine); } }, _ => { }, fault, budgetSeconds, mode)
                        .RunAsync(CancellationToken.None).GetAwaiter().GetResult();
                    File.WriteAllText(args[1], JsonSerializer.Serialize(new { log, results }, JsonOptions));
                    return;
                }
                if (args[0] == "--diagnose")
                {
                    var report = ConnectionDiagnostics.InspectAsync(null, null, CancellationToken.None).GetAwaiter().GetResult();
                    File.WriteAllText(args[1], report);
                    return;
                }
                if (args[0] == "--probe-one")
                {
                    var domain = args[2];
                    var ip = args[3];
                    var url = NetworkScanner.BackupUrls()[domain];
                    File.WriteAllText(args[1] + ".log", "");
                    int seconds = args.Length > 4 ? int.Parse(args[4]) : 20;
                    var result = NetworkScanner.TestNode(domain, ip, "持续测速验证", url, true, CancellationToken.None, seconds,
                        s => File.AppendAllText(args[1] + ".log", s + Environment.NewLine), compareDecoded: args.Length > 5 && bool.Parse(args[5])).GetAwaiter().GetResult();
                    File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
                    return;
                }
                if (args[0] == "--probe-parallel")
                {
                    var node = new NodeResult(args[2], args[3], "三路验证", false, 0, 0, 0, "", "", NetworkScanner.BackupUrls()[args[2]]);
                    var result = ParallelDownload.Measure(node, 20, CancellationToken.None).GetAwaiter().GetResult();
                    File.WriteAllText(args[1], JsonSerializer.Serialize(result, JsonOptions));
                    return;
                }
                if (args[0] == "--preview")
                {
                    ApplicationConfiguration.Initialize();
                    using var form = new MainForm();
                    form.Show();
                    if (args.Length > 2) form.PreviewReport(args[2]);
                    Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(args[1]);
                    return;
                }
                if (args[0] == "--preview-apply")
                {
                    ApplicationConfiguration.Initialize();
                    using var dialog = new OperationDialog(false);
                    dialog.Show();
                    dialog.UpdateProgress(new OperationProgress(30, "复核节点 2/6", "binaries-fallback.eveonline.com\r\n正在确认节点可下载，尚未修改 hosts。"));
                    Application.DoEvents();
                    using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(args[1]);
                    return;
                }
                if (args[0] is "--apply" or "--restore")
                {
                    ElevatedOperation.RunAsync(args[0], args[1]).GetAwaiter().GetResult();
                    return;
                }
                throw new ArgumentException("Unknown command.");
            }
            catch (Exception ex)
            {
                if (args.Length > 1 && args[0] is "--probe" or "--probe-fallback" or "--probe-mode" or "--probe-one" or "--diagnose" or "--self-test" or "--check-update")
                    File.WriteAllText(args[1], ex.ToString());
                Environment.ExitCode = 1;
                return;
            }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
