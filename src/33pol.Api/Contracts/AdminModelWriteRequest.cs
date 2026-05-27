using Pol33.Core.Models;

namespace Pol33.Api.Contracts;

public sealed class AdminModelWriteRequest
{
    public ModelConfig Model { get; set; } = new();

    /// <summary>Write-only upstream API key. Never returned on GET.</summary>
    public string? ApiKey { get; set; }

    /// <summary>When true on PATCH, removes stored upstream credential and clears upstreamAuth.</summary>
    public bool ClearApiKey { get; set; }
}
