using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class GatewayStatsService(IAdminSummaryReader summaryReader)
{
    public GatewayStatsSnapshot GetSnapshot()
    {
        var summary = summaryReader.GetSnapshot();
        return new GatewayStatsSnapshot
        {
            Uptime = summary.Uptime,
            UptimeSeconds = summary.UptimeSeconds,
            TotalRequests = summary.TotalInferenceRequests,
            RequestsPerModel = summary.RequestsPerModel,
            AverageLatencyMs = summary.AverageLatencyMs,
            ActiveConnections = summary.ActiveStreams,
            ErrorsPerModel = summary.ErrorsPerModel,
        };
    }
}
