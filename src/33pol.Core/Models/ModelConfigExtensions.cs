namespace Pol33.Core.Models;

public static class ModelConfigExtensions
{
    public static bool AllowsPublicGatewayAccess(this ModelConfig? model) =>
        model is not null && model.PublicAccess;

    /// <summary>
    /// True when an operator has taken this route out of service. Checked before health, grants and
    /// every other admission control: a stopped route is not a route the gateway will resolve.
    /// </summary>
    public static bool IsStopped(this ModelConfig? model) =>
        model is not null && ModelRouteStates.IsStopped(model.State);

    /// <summary>The inverse of <see cref="IsStopped"/>; a null model serves nothing.</summary>
    public static bool IsServing(this ModelConfig? model) =>
        model is not null && !model.IsStopped();
}
