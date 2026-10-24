using System.Diagnostics;

namespace EveUpdateHelper;

internal sealed class ScanBudget : IDisposable
{
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly CancellationTokenSource deadline;
    internal int TotalSeconds { get; }
    internal int SampleSeconds => TotalSeconds switch { 60 => 8, 120 => 10, 180 => 15, _ => 20 };
    internal int SurveySeconds => TotalSeconds switch { 60 => 14, 120 => 30, 180 => 45, _ => 70 };
    internal int MetadataSeconds => TotalSeconds switch { 60 => 6, 120 => 8, _ => 12 };
    internal double ElapsedSeconds => clock.Elapsed.TotalSeconds;
    internal double RemainingSeconds => Math.Max(0, TotalSeconds - 1 - ElapsedSeconds);
    internal CancellationToken Token => deadline.Token;
    internal bool CanStartSample(double remainingSeconds) => remainingSeconds >= SampleSeconds + 5;
    internal ScanBudget(int totalSeconds, CancellationToken ct)
    {
        if (totalSeconds is not (0 or 60 or 120 or 180 or 300 or 480)) throw new ArgumentOutOfRangeException(nameof(totalSeconds));
        TotalSeconds = totalSeconds;
        deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Leave one second for cancellation cleanup and delivering the report.
        if (totalSeconds > 0) deadline.CancelAfter(TimeSpan.FromSeconds(totalSeconds - 1));
    }
    public void Dispose() => deadline.Dispose();
}
