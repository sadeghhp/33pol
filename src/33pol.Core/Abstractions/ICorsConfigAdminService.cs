using Pol33.Core.Configuration;

namespace Pol33.Core.Abstractions;

public interface ICorsConfigAdminService
{
    string[] GetCurrent();

    Task<CorsConfigUpdateResult> UpdateAsync(
        IReadOnlyList<string> allowedOrigins,
        CancellationToken cancellationToken = default);
}
