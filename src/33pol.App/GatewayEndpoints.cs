using System.Reflection;

namespace Pol33.App;

internal static class GatewayEndpoints
{
    public static IResult GetRoot()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        return Results.Json(new
        {
            name = "33pol",
            version,
            documentation = new
            {
                implementationPlan = "docs/implementation-plan/README.md",
                architecture = "docs/implementation-plan/01-solution-architecture.md",
            },
        });
    }
}
