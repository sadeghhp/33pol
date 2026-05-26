using System.Reflection;
using NetArchTest.Rules;

namespace Pol33.Architecture.Tests;

public sealed class DependencyRulesTests
{
    private static readonly Assembly CoreAssembly = typeof(Pol33.Core.Configuration.GatewayOptions).Assembly;
    private static readonly Assembly RegistryAssembly = typeof(Pol33.Registry.Services.ModelRegistryService).Assembly;
    private static readonly Assembly ProxyAssembly = typeof(Pol33.Proxy.Middleware.ModelRouterMiddleware).Assembly;
    private static readonly Assembly ApiAssembly = typeof(Pol33.Api.Endpoints.ConfigAdminEndpoints).Assembly;
    private static readonly Assembly OperatorConsoleAssembly = typeof(Pol33.OperatorConsole.ProjectStub).Assembly;
    private static readonly Assembly PersistenceAssembly = typeof(Pol33.Persistence.ProjectStub).Assembly;
    private static readonly Assembly ObservabilityAssembly = typeof(Pol33.Observability.ProjectStub).Assembly;
    private static readonly Assembly BillingAssembly = typeof(Pol33.Billing.ProjectStub).Assembly;

    private static readonly Assembly[] FeatureAssemblies =
    [
        RegistryAssembly,
        ProxyAssembly,
        ApiAssembly,
        typeof(Pol33.Policy.ProjectStub).Assembly,
        ObservabilityAssembly,
        BillingAssembly,
        PersistenceAssembly,
        typeof(Pol33.Security.ProjectStub).Assembly,
        OperatorConsoleAssembly,
    ];

    [Fact]
    public void Core_ShouldNotReferenceAspNetEfOrYarp()
    {
        var result = Types.InAssembly(CoreAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "Yarp")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void Registry_ShouldNotReferenceHttpPipelineTypes()
    {
        var result = Types.InAssembly(RegistryAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Microsoft.AspNetCore.Http",
                "Yarp",
                "Yarp.ReverseProxy")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void Proxy_ShouldNotReferencePersistence()
    {
        var result = Types.InAssembly(ProxyAssembly)
            .Should()
            .NotHaveDependencyOn("33pol.Persistence")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void Api_ShouldOnlyReferenceCoreAmongFeatureLibraries()
    {
        var result = Types.InAssembly(ApiAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "33pol.Proxy",
                "33pol.Security",
                "33pol.Policy",
                "33pol.Registry",
                "33pol.Persistence",
                "33pol.Billing",
                "33pol.Observability",
                "33pol.OperatorConsole",
                "33pol.App")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void OperatorConsole_ShouldNotReferenceAspNetYarpProxyOrApi()
    {
        var result = Types.InAssembly(OperatorConsoleAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.AspNetCore",
                "Yarp",
                "Yarp.ReverseProxy",
                "33pol.Proxy",
                "33pol.Api")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void Policy_ShouldNotReferenceProxyOrRegistry()
    {
        var result = Types.InAssembly(typeof(Pol33.Policy.ProjectStub).Assembly)
            .Should()
            .NotHaveDependencyOnAny("33pol.Proxy", "33pol.Registry")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void Persistence_ShouldOnlyReferenceCore()
    {
        var result = Types.InAssembly(PersistenceAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "33pol.Proxy",
                "33pol.Security",
                "33pol.Policy",
                "33pol.Registry",
                "33pol.Billing",
                "33pol.Observability",
                "33pol.Api",
                "33pol.OperatorConsole",
                "33pol.App")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void Observability_ShouldOnlyReferenceCore()
    {
        var result = Types.InAssembly(ObservabilityAssembly)
            .Should()
            .NotHaveDependencyOnAny(
                "33pol.Proxy",
                "33pol.Security",
                "33pol.Policy",
                "33pol.Registry",
                "33pol.Persistence",
                "33pol.Billing",
                "33pol.Api",
                "33pol.OperatorConsole",
                "33pol.App")
            .GetResult();

        AssertArchitectureRule(result);
    }

    [Fact]
    public void FeatureAssemblies_ShouldNotHaveCircularReferences()
    {
        var names = FeatureAssemblies.Select(a => a.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        var graph = FeatureAssemblies.ToDictionary(
            a => a.GetName().Name!,
            a => a.GetReferencedAssemblies()
                .Select(r => r.Name!)
                .Where(name => names.Contains(name))
                .ToArray());

        foreach (var start in graph.Keys)
        {
            AssertNoCycleFrom(start, graph, []);
        }
    }

    private static void AssertNoCycleFrom(
        string node,
        IReadOnlyDictionary<string, string[]> graph,
        HashSet<string> visited)
    {
        if (!visited.Add(node))
        {
            Assert.Fail($"Circular project reference detected involving '{node}'.");
        }

        if (graph.TryGetValue(node, out var edges))
        {
            foreach (var next in edges)
            {
                AssertNoCycleFrom(next, graph, visited);
            }
        }

        visited.Remove(node);
    }

    private static void AssertArchitectureRule(TestResult result)
    {
        var failures = result.FailingTypes ?? [];
        failures.Should().BeEmpty($"architecture rule failed: {string.Join(", ", failures)}");
    }
}
