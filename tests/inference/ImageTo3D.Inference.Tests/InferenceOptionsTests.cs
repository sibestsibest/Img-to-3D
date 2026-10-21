using ImageTo3D.Inference.Configuration;

namespace ImageTo3D.Inference.Tests;

public class InferenceOptionsTests
{
    [Fact]
    public void EffectiveIntraOpThreads_DefaultsToEightyPercentOfCores()
    {
        var options = new InferenceOptions();

        var expected = Math.Max(1, (int)Math.Floor(Environment.ProcessorCount * 0.8));
        Assert.Equal(expected, options.EffectiveIntraOpThreads);
        Assert.InRange(options.EffectiveIntraOpThreads, 1, Environment.ProcessorCount);
    }

    [Fact]
    public void EffectiveIntraOpThreads_ExplicitCountWins()
    {
        var options = new InferenceOptions { IntraOpThreads = 3, CpuLimit = 0.1 };

        Assert.Equal(3, options.EffectiveIntraOpThreads);
    }

    [Fact]
    public void EffectiveIntraOpThreads_NeverBelowOne()
    {
        var options = new InferenceOptions { CpuLimit = 0.05 };

        Assert.True(options.EffectiveIntraOpThreads >= 1);
    }
}
