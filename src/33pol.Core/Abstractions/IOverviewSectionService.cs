using Pol33.Core.Models.Overview;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Builds the database-backed Overview sections. Each call serves a memoised result until the
/// configured TTL lapses (or <paramref name="refresh"/> forces a rebuild) and returns null when the
/// section's data source is not configured — a gateway with no database has no FinOps to show.
/// </summary>
public interface IOverviewSectionService
{
    Task<FinOpsOverview?> GetFinOpsAsync(bool refresh, CancellationToken cancellationToken);

    Task<PolicyOverview?> GetPolicyAsync(bool refresh, CancellationToken cancellationToken);

    Task<ControlPlaneOverview?> GetControlPlaneAsync(bool refresh, CancellationToken cancellationToken);

    Task<ActivityOverview?> GetActivityAsync(int limit, bool refresh, CancellationToken cancellationToken);

    Task<TenantsOverview?> GetTenantsAsync(bool refresh, CancellationToken cancellationToken);
}
