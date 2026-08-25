using Pol33.Core.Models.Overview;

namespace Pol33.Core.Abstractions;

/// <summary>Turns the Overview's sections into the ranked list of things that need an operator.</summary>
public interface IAttentionEvaluator
{
    IReadOnlyList<AttentionItem> Evaluate(AttentionInputs inputs);
}

/// <summary>Everything the evaluator may look at; any section may be null when its producer is not wired.</summary>
public sealed record AttentionInputs
{
    public DateTimeOffset Now { get; init; }

    public IReadOnlyList<WindowStats>? Windows { get; init; }

    public IReadOnlyList<BackendOverview>? Backends { get; init; }

    public PipelineOverview? Pipeline { get; init; }

    public FinOpsOverview? FinOps { get; init; }

    public PolicyOverview? Policy { get; init; }

    public ControlPlaneOverview? ControlPlane { get; init; }

    public TenantsOverview? Tenants { get; init; }

    public bool DatabaseConfigured { get; init; }
}
