namespace Pol33.Api.Contracts;

/// <summary>
/// GET response and PUT request body for <c>/admin/api/cors</c>.
/// </summary>
public sealed class AdminCorsDto
{
    public string[] AllowedOrigins { get; set; } = [];
}
