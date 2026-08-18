using Pol33.App.DependencyInjection;
using Pol33.Core.Configuration;

namespace Pol33.Integration.Tests.Configuration;

/// <summary>
/// Environment CORS origins apply on every boot and survive every database load, rather than only
/// seeding the database once.
/// </summary>
public sealed class GatewayConfigStateEnvironmentOriginsTests
{
    [Fact]
    public void Constructor_OverlaysEnvironmentOriginsOnTheInitialSnapshot()
    {
        var initial = GatewayConfigSnapshot.Defaults with
        {
            Cors = new CorsConfigSection { AllowedOrigins = ["https://from-appsettings.example"] },
        };

        var state = new GatewayConfigState(initial, ["https://from-env.example/"]);

        state.Current.Cors.AllowedOrigins.Should().Equal(
            "https://from-env.example",
            "https://from-appsettings.example");
    }

    [Fact]
    public void Set_OverlaysEnvironmentOriginsOnEveryDatabaseSnapshot()
    {
        var state = new GatewayConfigState(GatewayConfigSnapshot.Defaults, ["https://from-env.example"]);

        state.Set(GatewayConfigSnapshot.Defaults with
        {
            Version = 3,
            Cors = new CorsConfigSection { AllowedOrigins = ["https://from-db.example", "https://from-env.example"] },
        });

        state.Current.Version.Should().Be(3);
        state.Current.Cors.AllowedOrigins.Should().Equal("https://from-env.example", "https://from-db.example");
    }

    [Fact]
    public void Set_WithoutEnvironmentOrigins_KeepsTheSnapshotAsIs()
    {
        var state = new GatewayConfigState(GatewayConfigSnapshot.Defaults);
        var snapshot = GatewayConfigSnapshot.Defaults with
        {
            Cors = new CorsConfigSection { AllowedOrigins = ["https://from-db.example"] },
        };

        state.Set(snapshot);

        state.Current.Should().BeSameAs(snapshot);
    }
}
