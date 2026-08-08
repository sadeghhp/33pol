using FluentAssertions;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class RegistryGateTests
{
    [Fact]
    public async Task WaitAsync_MarksGateHeldUntilRelease()
    {
        var gate = new RegistryGate();

        gate.IsHeld.Should().BeFalse();
        await gate.WaitAsync();
        gate.IsHeld.Should().BeTrue();
        gate.Release();
        gate.IsHeld.Should().BeFalse();
    }

    [Fact]
    public async Task TryEnter_WhenHeld_ReturnsFalse()
    {
        var gate = new RegistryGate();
        await gate.WaitAsync();

        gate.TryEnter().Should().BeFalse();

        gate.Release();
        gate.TryEnter().Should().BeTrue();
        gate.Release();
    }
}
