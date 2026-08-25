using System.Net;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The admin console is a static asset bundle, so nothing else in the suite would notice if the
/// stop control were dropped from the models table or wired to the wrong endpoint — the backend
/// tests would keep passing while operators lost the only way to reach it.
/// </summary>
public sealed class AdminConsoleModelStopActionTests
{
    private static async Task<string> GetAssetAsync(string path)
    {
        using var factory = GatewayWebApplicationFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task ModelsTable_OffersStopAndStartControls()
    {
        var html = await GetAssetAsync("/admin/index.html");

        html.Should().Contain("aria-label=\"Stop model\"", "operators need a way to stop a route");
        html.Should().Contain("aria-label=\"Start model\"", "and a way to put it back");

        // Only one of the pair is offered at a time, driven by the route's current state.
        html.Should().Contain("x-show=\"m.isServing\"");
        html.Should().Contain("x-show=\"m.isStopped\"");
    }

    [Fact]
    public async Task ModelsTable_ShowsEachRoutesState()
    {
        var html = await GetAssetAsync("/admin/index.html");

        html.Should().Contain("<th>State</th>");
        html.Should().Contain("x-text=\"m.stateText\"");
    }

    [Fact]
    public async Task StopControl_CallsTheStopEndpoint_BehindAConfirmation()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("confirmStopModel");
        js.Should().Contain("setModelState");
        // The action posts to the dedicated endpoint rather than PATCHing the whole model.
        js.Should().Contain("'/admin/api/models/' + encodeURIComponent(id) + '/' + action");
        js.Should().Contain("method: 'POST'");
    }

    /// <summary>
    /// The console runs on Alpine's CSP build, which resolves only plain dotted paths in
    /// attributes. A bracket lookup such as <c>icons['play-circle']</c> silently renders nothing,
    /// so the start button would be an invisible, unclickable cell.
    /// </summary>
    [Fact]
    public async Task StateControls_UseIconsTheCspBuildCanResolve()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var icons = await GetAssetAsync("/admin/admin-icons.js");

        html.Should().Contain("x-html=\"icons.pause\"");
        html.Should().Contain("x-html=\"icons.play-circle\"");
        html.Should().NotContain("icons['");

        icons.Should().Contain("pause:");
        icons.Should().Contain("'play-circle':");
    }
}
