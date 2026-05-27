using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ModelRegistryPersistErrorsTests
{
    [Fact]
    public void FromException_IOExceptionBusy_Returns503WithFriendlyMessage()
    {
        var result = ModelRegistryPersistErrors.FromException(
            new IOException("Device or resource busy : '/app/models.json'"),
            "/app/models.json",
            NullLogger.Instance,
            "add model");

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(503);
        result.Message.Should().Contain("deploy/docker/config/models.json");
    }

    [Fact]
    public void FromException_JsonException_Returns400()
    {
        var result = ModelRegistryPersistErrors.FromException(
            new System.Text.Json.JsonException("duplicate id"),
            "/app/models.json",
            NullLogger.Instance,
            "add model");

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(400);
    }

    [Fact]
    public void FormatIOException_BusyMessage_IncludesDockerHint()
    {
        var message = ModelRegistryPersistErrors.FormatIOException(
            new IOException("Device or resource busy : '/app/models.json'"),
            "/app/models.json");

        message.Should().Contain("read-only");
        message.Should().Contain("deploy/docker/config/models.json");
    }

    [Fact]
    public void FormatIOException_GenericMessage_IncludesPath()
    {
        var message = ModelRegistryPersistErrors.FormatIOException(
            new IOException("disk full"),
            "/data/models.json");

        message.Should().Contain("/data/models.json");
        message.Should().Contain("disk full");
    }
}
