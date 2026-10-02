using Ssvep.Speller.Core.Classification;

namespace Ssvep.Speller.Tests;

public sealed class ReferenceSignalTests
{
    [Fact]
    public void ReferenceMatrixContainsSinCosPairsForEachHarmonic()
    {
        double[,] reference = ReferenceSignalGenerator.Generate(
            frequencyHz: 10.0,
            samplingRateHz: 250,
            sampleCount: 25,
            harmonicCount: 3,
            phaseRadians: 0.0);

        Assert.Equal(25, reference.GetLength(0));
        Assert.Equal(6, reference.GetLength(1));
        Assert.Equal(0.0, reference[0, 0], 6);
        Assert.Equal(1.0, reference[0, 1], 6);
        Assert.Equal(Math.Sin(2.0 * Math.PI * 10.0 / 250.0), reference[1, 0], 6);
        Assert.Equal(Math.Cos(2.0 * Math.PI * 10.0 / 250.0), reference[1, 1], 6);
        Assert.Equal(Math.Sin(2.0 * Math.PI * 20.0 / 250.0), reference[1, 2], 6);
        Assert.Equal(Math.Cos(2.0 * Math.PI * 20.0 / 250.0), reference[1, 3], 6);
    }
}
