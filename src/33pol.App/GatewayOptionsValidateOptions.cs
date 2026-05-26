using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.App;

internal sealed class GatewayOptionsValidateOptions : IValidateOptions<GatewayOptions>
{
    private readonly IHostEnvironment _environment;

    public GatewayOptionsValidateOptions(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var errors = GatewayOptionsValidation.Validate(options, _environment.IsProduction());
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
