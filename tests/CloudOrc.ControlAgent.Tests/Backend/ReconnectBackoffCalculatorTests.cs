using CloudOrc.ControlAgent.Backend;
using CloudOrc.ControlAgent.Configuration;

namespace CloudOrc.ControlAgent.Tests.Backend;

public class ReconnectBackoffCalculatorTests
{
    private static BackendConnectionOptions MakeOptions(int initial = 2, int max = 60) => new()
    {
        ReconnectInitialDelaySeconds = initial,
        ReconnectMaximumDelaySeconds = max
    };

    [Fact]
    public void NextDelay_FirstCall_ReturnsInitialDelay()
    {
        var calculator = new ReconnectBackoffCalculator(MakeOptions(initial: 2));

        var delay = calculator.NextDelay();

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void NextDelay_DoublesOnEachConsecutiveCall()
    {
        var calculator = new ReconnectBackoffCalculator(MakeOptions(initial: 2, max: 1000));

        Assert.Equal(TimeSpan.FromSeconds(2), calculator.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(4), calculator.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(8), calculator.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(16), calculator.NextDelay());
    }

    [Fact]
    public void NextDelay_NeverExceedsConfiguredMaximum()
    {
        var calculator = new ReconnectBackoffCalculator(MakeOptions(initial: 2, max: 10));

        calculator.NextDelay(); // 2
        calculator.NextDelay(); // 4
        calculator.NextDelay(); // 8
        var fourth = calculator.NextDelay(); // would be 16, capped at 10
        var fifth = calculator.NextDelay(); // would be 32, capped at 10

        Assert.Equal(TimeSpan.FromSeconds(10), fourth);
        Assert.Equal(TimeSpan.FromSeconds(10), fifth);
    }

    [Fact]
    public void Reset_RestartsFromInitialDelay()
    {
        var calculator = new ReconnectBackoffCalculator(MakeOptions(initial: 2, max: 1000));
        calculator.NextDelay();
        calculator.NextDelay();
        calculator.NextDelay();

        calculator.Reset();
        var delay = calculator.NextDelay();

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void NeverReconnectsInATightLoop_FirstDelayIsAlwaysPositive()
    {
        // Guards the explicit requirement: never reconnect immediately / in a tight loop.
        var calculator = new ReconnectBackoffCalculator(MakeOptions(initial: 2));

        var delay = calculator.NextDelay();

        Assert.True(delay > TimeSpan.Zero);
    }
}
