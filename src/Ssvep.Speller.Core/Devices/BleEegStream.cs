using Ssvep.Speller.Core.Signal;

namespace Ssvep.Speller.Core.Devices;

public sealed class BleEegStream : IEegStream
{
    public int SamplingRateHz => 250;

    public int ChannelCount => 8;

    public IAsyncEnumerable<EegSample> ReadSamplesAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException("BLE/GATT device parsing is reserved for V2 after protocol details are provided.");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
