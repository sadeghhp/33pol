[assembly: FluentAssertions.Extensibility.AssertionEngineInitializer(
    typeof(Pol33.Tests.FluentAssertionsLicenseAcknowledgment),
    nameof(Pol33.Tests.FluentAssertionsLicenseAcknowledgment.Acknowledge))]

namespace Pol33.Tests;

/// <summary>
/// Suppresses the Fluent Assertions 8.x commercial-use reminder on each test run.
/// Non-commercial / open-source use is free; commercial use requires an Xceed license.
/// </summary>
public static class FluentAssertionsLicenseAcknowledgment
{
    public static void Acknowledge() => FluentAssertions.License.Accepted = true;
}
