using Pol33.Core.Models;

namespace Pol33.Api.Contracts;

public sealed class AdminModelListItem
{
    public required ModelConfig Model { get; init; }

    public bool HasUpstreamCredential { get; init; }
}
