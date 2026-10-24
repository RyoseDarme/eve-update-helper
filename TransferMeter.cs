namespace EveUpdateHelper;

internal sealed class TransferMeter(double warmupSeconds)
{
    internal long Bytes { get; private set; }
    internal long ScoredBytes { get; private set; }
    internal void Observe(double elapsedSeconds, int bytes)
    {
        if (bytes < 0 || elapsedSeconds < 0) throw new ArgumentOutOfRangeException();
        Bytes += bytes;
        if (elapsedSeconds >= warmupSeconds) ScoredBytes += bytes;
    }
    internal double MeasuredSeconds(double elapsedSeconds) => Math.Max(0, elapsedSeconds - warmupSeconds);
    internal double Speed(double elapsedSeconds) => ScoredBytes / Math.Max(MeasuredSeconds(elapsedSeconds), 0.001) / 1024 / 1024;
    internal bool Sufficient(double elapsedSeconds, int targetSeconds) =>
        ScoredBytes >= 128 * 1024 && MeasuredSeconds(elapsedSeconds) >= targetSeconds - 0.5;
}
