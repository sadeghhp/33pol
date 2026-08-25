using Pol33.Core.Models.Overview;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Produces the cheap, in-memory Overview sections that ride in every summary frame. Implemented
/// by the composition root, which can see the usage writer, policy tracker and config reload state
/// that the Observability module cannot.
/// </summary>
public interface IOverviewHotSectionSource
{
    PipelineOverview? GetPipeline();

    PolicyLiveOverview? GetPolicy();

    ControlPlaneLiveOverview? GetControlPlane();
}
