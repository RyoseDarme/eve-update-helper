namespace EveUpdateHelper;

internal enum ScanMode { Quick, Standard, Precise }
internal sealed record ScanProfile(ScanMode Mode, string Name, int TotalSeconds, int SampleSeconds,
    int SurveySeconds, int TcpRepeats, int MetadataSeconds)
{
    internal static ScanProfile For(ScanMode mode) => mode switch
    {
        ScanMode.Quick => new(mode, "快速", 180, 10, 0, 4, 0),
        ScanMode.Standard => new(mode, "标准", 480, 20, 45, 4, 20),
        _ => new(mode, "精校准", 0, 20, 180, 6, 45)
    };
}

internal static class Calibration
{
    internal static NodeResult Combine(NodeResult original, IReadOnlyList<NodeResult> rounds)
    {
        var valid = rounds.Where(r => r.Success && r.SmallFileMBps > 0 && r.MeasuredSeconds >= r.RequiredSeconds - .5).ToArray();
        if (valid.Length != 3) return original with { Success = false, Detail = $"精校准仅 {valid.Length}/3 轮通过，不可应用", CalibrationRounds = valid.Length };
        var speeds = valid.Select(r => r.SpeedMBps).Order().ToArray();
        double median = speeds[1], minimum = speeds[0];
        return original with
        {
            Success = true, SpeedMBps = median, SmallFileMBps = valid.Select(r => r.SmallFileMBps).Order().ElementAt(1),
            CalibrationRounds = 3, CalibrationSpeeds = speeds, QualityScore = .7 * median + .3 * minimum,
            Detail = $"精校准 3轮：中位 {median:F2}，最低 {minimum:F2}，最高 {speeds[2]:F2} MiB/s"
        };
    }
}
