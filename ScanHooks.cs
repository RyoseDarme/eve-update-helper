namespace EveUpdateHelper;

// Deterministic integration fixtures; production leaves every delegate unset.
internal sealed class ScanHooks
{
    internal Func<CancellationToken, Task<CdnCatalog>>? Catalog { get; init; }
    internal Func<string, CancellationToken, Task<Dictionary<string, string>>>? Dns { get; init; }
    internal Func<string, int, CancellationToken, Task<TcpSample>>? Tcp { get; init; }
    internal Func<NodeResult, CancellationToken, Task<HttpsSample>>? Https { get; init; }
    internal Func<NodeResult, CancellationToken, Task<SurveySample>>? Survey { get; init; }
    internal Func<NodeResult, int, bool, CancellationToken, Task<NodeResult>>? Download { get; init; }
    internal Func<NodeResult, CancellationToken, Task<ParallelSample>>? Parallel { get; init; }
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}
