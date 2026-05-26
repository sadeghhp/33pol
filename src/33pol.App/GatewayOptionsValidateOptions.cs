using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.App;

internal sealed class GatewayOptionsValidateOptions : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var errors = GatewayOptionsValidation.Validate(options);
        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
