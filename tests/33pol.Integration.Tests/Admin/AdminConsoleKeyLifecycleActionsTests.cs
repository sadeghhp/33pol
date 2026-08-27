using System.Net;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The admin console is a static asset bundle, so nothing else in the suite would notice if the
/// archive or delete controls were dropped from the keys table or wired to the wrong endpoint — the
/// endpoint tests would keep passing while operators lost the only way to reach them.
/// </summary>
public sealed class AdminConsoleKeyLifecycleActionsTests
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
    public async Task KeysTable_OffersArchiveRestoreAndDelete()
    {
        var html = await GetAssetAsync("/admin/index.html");

        html.Should().Contain("aria-label=\"Archive key\"");
        html.Should().Contain("aria-label=\"Restore key to the list\"");
        html.Should().Contain("aria-label=\"Delete key permanently\"");

        // Each control is offered only in the state it applies to.
        html.Should().Contain("x-show=\"k.canArchive\"");
        html.Should().Contain("x-show=\"k.canUnarchive\"");
        html.Should().Contain("x-show=\"k.canDelete\"");
    }

    [Fact]
    public async Task StatusFilter_OffersArchivedKeys()
    {
        var html = await GetAssetAsync("/admin/index.html");

        html.Should().Contain("<option value=\"archived\">");
    }

    /// <summary>
    /// Revoke stops the credential and leaves the key and its history behind; delete removes the row.
    /// Rendering both with the same trash icon would make the irreversible action look like the other.
    /// </summary>
    [Fact]
    public async Task RevokeAndDelete_UseDistinctIcons()
    {
        var html = await GetAssetAsync("/admin/index.html");

        html.Should().Contain("aria-label=\"Revoke key\"><span class=\"icon\" x-html=\"icons.shield-off\">");
        html.Should().Contain("aria-label=\"Delete key permanently\"><span class=\"icon\" x-html=\"icons.trash\">");
    }

    /// <summary>
    /// The console runs on Alpine's CSP build, which resolves only plain dotted paths in attributes.
    /// A bracket lookup such as <c>icons['archive-out']</c> silently renders nothing, so the control
    /// would be an invisible, unclickable cell.
    /// </summary>
    [Fact]
    public async Task LifecycleControls_UseIconsTheCspBuildCanResolve()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var icons = await GetAssetAsync("/admin/admin-icons.js");

        html.Should().Contain("x-html=\"icons.archive\"");
        html.Should().Contain("x-html=\"icons.archive-out\"");
        html.Should().Contain("x-html=\"icons.shield-off\"");
        html.Should().NotContain("icons['");

        icons.Should().Contain("archive:");
        icons.Should().Contain("'archive-out':");
        icons.Should().Contain("'shield-off':");
    }

    [Fact]
    public async Task Actions_CallTheLifecycleEndpoints()
    {
        var js = await GetAssetAsync("/admin/admin-app.js");

        js.Should().Contain("'/admin/api/keys/' + id + '/archive'");
        js.Should().Contain("'/admin/api/keys/' + id + '/unarchive'");
        js.Should().Contain("method: 'DELETE'");
        js.Should().Contain("confirmKeyPrefix");
        // Archived keys come down with the rest, so the Archived filter needs no second round trip.
        js.Should().Contain("includeArchived=true");
    }

    /// <summary>
    /// The delete dialog must not be able to submit until the operator has typed the key's prefix,
    /// and the button's disabled state must come from a getter — the CSP build cannot evaluate an
    /// expression in the attribute.
    /// </summary>
    [Fact]
    public async Task DeleteDialog_IsGatedOnTypingThePrefix()
    {
        var html = await GetAssetAsync("/admin/index.html");
        var js = await GetAssetAsync("/admin/admin-app.js");

        html.Should().Contain("id=\"delete-key-title\"");
        html.Should().Contain("x-model=\"mdl.deleteConfirmText\"");
        html.Should().Contain(":disabled=\"deleteConfirmDisabled\"");

        js.Should().Contain("get deleteConfirmDisabled()");
        js.Should().Contain("this.deleteConfirmText.trim() === this.deleteConfirmKey.keyPrefix");
        // x-model needs a {get,set} pair registered on mdl, or typing does nothing.
        js.Should().Contain("deleteConfirmText: b('deleteConfirmText')");
    }

    /// <summary>
    /// A 409 from a lifecycle transition is an expected, informative outcome — the server has already
    /// written the sentence the operator needs. Falling through to the generic handler would show
    /// "409 Conflict" instead.
    /// </summary>
    [Fact]
    public async Task LifecycleConflicts_SurfaceTheServersOwnMessage()
    {
        var js = await GetAssetAsync("/admin/admin-errors.js");

        js.Should().Contain("status === 409 && json?.code && json?.message");
        js.Should().Contain("key_has_usage");
    }

    /// <summary>
    /// The Status cell clips (<c>overflow: hidden</c>) and the chip inside it is <c>nowrap</c>, so the
    /// column has to be wide enough for the longest word it can hold. It was not, and "Revoked" was
    /// already being cut off before "Archived" was added.
    /// </summary>
    [Fact]
    public async Task ArchivedStatusChip_HasItsOwnStyle()
    {
        var css = await GetAssetAsync("/admin/admin.css");

        css.Should().Contain(".status-chip.muted");
    }
}
