using Ssvep.Speller.Core.Devices;
using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Tests;

public sealed class StudioDiagnosticsTests
{
    [Fact]
    public void EegWithoutImpedanceCannotRefreshCachedContactData()
    {
        var diagnostics = new SynchroniBridgeDiagnostics();
        var first = DateTimeOffset.UtcNow.AddSeconds(-10);
        diagnostics.RecordSample(new(new EegSample(1, first, new double[8]), false, 250, 8, Enumerable.Repeat(100_000, 8).ToArray()));
        Assert.Equal(first, diagnostics.CreateSnapshot().ImpedanceReceivedAt);
        diagnostics.RecordSample(new(new EegSample(2, first.AddSeconds(10), new double[8]), false, 250, 8, []));
        Assert.Equal(first, diagnostics.CreateSnapshot().ImpedanceReceivedAt);
        Assert.Equal(first.AddSeconds(10), diagnostics.CreateSnapshot().LastSampleAt);
    }
}
