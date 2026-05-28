using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Policy.Admin;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.Admin;

public sealed class RateLimitConfigAdminServiceTests
{
    [Fact]
    public async Task UpdateAsync_ValidPayload_PersistsAndReloadsOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "33pol-rate-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var appsettingsPath = Path.Combine(root, "appsettings.json");
        await File.WriteAllTextAsync(
            appsettingsPath,
            """
            {
              "RateLimiting": {
                "Default": { "Rpm": 10, "Burst": 1, "MaxConcurrentStreams": 1 },
                "Plans": { "standard": { "Rpm": 20, "Burst": 2, "MaxConcurrentStreams": 2 } },
                "Tenants": { "tenant-a": { "Rpm": 99, "Burst": 9, "MaxConcurrentStreams": 9 } }
              }
            }
            """);

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(root)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var monitor = new TestOptionsMonitor(
                configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
                ?? new RateLimitingOptions());

            configuration.GetReloadToken().RegisterChangeCallback(
                _ =>
                {
                    monitor.CurrentValue =
                        configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
                        ?? new RateLimitingOptions();
                },
                null);

            var service = new RateLimitConfigAdminService(
                configuration,
                new TestHostEnvironment(root),
                monitor,
                NullLogger<RateLimitConfigAdminService>.Instance);

            var result = await service.UpdateAsync(
                new RateLimitTierOptions { Rpm = 30, Burst = 3, MaxConcurrentStreams = 3 },
                new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["enterprise"] = new() { Rpm = 300, Burst = 30, MaxConcurrentStreams = 30 },
                },
                CancellationToken.None);

            result.Success.Should().BeTrue();

            var persisted = await File.ReadAllTextAsync(appsettingsPath);
            persisted.Should().Contain("\"Rpm\": 30");
            persisted.Should().Contain("enterprise");
            persisted.Should().Contain("tenant-a");

            var resolver = new RateLimitPolicyResolver(monitor);
            resolver.Resolve("enterprise", null).Rpm.Should().Be(300);
            resolver.Resolve(null, "tenant-a").Rpm.Should().Be(99);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAsync_InvalidRpm_ReturnsValidationError()
    {
        var root = Path.Combine(Path.GetTempPath(), "33pol-rate-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(root)
                .AddInMemoryCollection()
                .Build();

            var service = new RateLimitConfigAdminService(
                configuration,
                new TestHostEnvironment(root),
                new TestOptionsMonitor(new RateLimitingOptions()),
                NullLogger<RateLimitConfigAdminService>.Instance);

            var result = await service.UpdateAsync(
                new RateLimitTierOptions { Rpm = 0, Burst = 0, MaxConcurrentStreams = 0 },
                new Dictionary<string, RateLimitTierOptions>(),
                CancellationToken.None);

            result.Success.Should().BeFalse();
            result.StatusCode.Should().Be(400);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestOptionsMonitor(RateLimitingOptions initial) : IOptionsMonitor<RateLimitingOptions>
    {
        public RateLimitingOptions CurrentValue { get; set; } = initial;

        public RateLimitingOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RateLimitingOptions, string?> listener) => null;
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "33pol.Policy.Tests";

        public string ContentRootPath { get; set; } = contentRoot;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
