namespace Pol33.Core.Models;

public static class ModelConfigExtensions
{
    public static bool AllowsPublicGatewayAccess(this ModelConfig? model) =>
        model is not null && model.PublicAccess;
}
