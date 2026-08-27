using Microsoft.EntityFrameworkCore;
using Pol33.Core.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Billing;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Persistence;
using Pol33.Persistence.Repositories;
using TenantRepository = Pol33.Persistence.Repositories.TenantRepository;
using Pol33.Security.Configuration;
using Pol33.Security.Services;

namespace Pol33.Security.Tests.Services;

public sealed class AdminKeyServiceTests
{
    private const string Pepper = "test-pepper";

    [Fact]
    public async Task CreateAsync_ReturnsSecretOnce_ListOmitsSecret()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
        var created = await sut.CreateAsync(
            tenantId,
            new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

        created.Secret.Should().StartWith("sk-33pol-");
        var list = await sut.ListAsync(tenantId);
        list.Should().ContainSingle(item => item.Id == created.Id);
        list.Single().KeyPrefix.Should().Be(created.KeyPrefix);
        }
    }

    [Fact]
    public async Task RevokeAsync_InvalidatesSubsequentValidation()
    {
        var (sut, tenantId, validator, db) = await CreateSutAsync();
        await using (db)
        {
        var created = await sut.CreateAsync(
            tenantId,
            new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

        (await validator.ValidateAsync(created.Secret, CancellationToken.None)).IsSuccess.Should().BeTrue();

        await sut.RevokeAsync(tenantId, created.Id);

        (await validator.ValidateAsync(created.Secret, CancellationToken.None)).IsSuccess.Should().BeFalse();
        }
    }

    [Fact]
    public async Task CreateAsync_WithMetadata_PersistsOnList()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest
                {
                    Role = ApiKeyRole.Inference,
                    Label = "bot",
                    Assignee = "Platform",
                    CostCenter = "eng",
                });

            var list = await sut.ListAsync(tenantId);
            var item = list.Single(x => x.Id == created.Id);
            item.Label.Should().Be("bot");
            item.Assignee.Should().Be("Platform");
            item.CostCenter.Should().Be("eng");
        }
    }

    [Fact]
    public async Task UpdateAsync_RevokedKey_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.UpdateAsync(
                tenantId,
                created.Id,
                new UpdateAdminApiKeyRequest { Label = "x" });

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task RevokeManyAsync_RevokesExistingTenantKeys_AndSkipsInvalidIds()
    {
        var (sut, tenantId, validator, db) = await CreateSutAsync();
        await using (db)
        {
            var first = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });
            var second = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

            var revokedCount = await sut.RevokeManyAsync(
                tenantId,
                [first.Id, second.Id, Guid.Empty, first.Id, Guid.NewGuid()]);

            revokedCount.Should().Be(2);
            (await validator.ValidateAsync(first.Secret, CancellationToken.None)).IsSuccess.Should().BeFalse();
            (await validator.ValidateAsync(second.Secret, CancellationToken.None)).IsSuccess.Should().BeFalse();
        }
    }

    [Fact]
    public async Task RevokeManyAsync_EmptyKeyIds_ReturnsZero()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var revokedCount = await sut.RevokeManyAsync(tenantId, []);

            revokedCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task UpdateAsync_UpdatesMetadata()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference, Label = "before" });

            var updated = await sut.UpdateAsync(
                tenantId,
                created.Id,
                new UpdateAdminApiKeyRequest
                {
                    Label = "after",
                    Assignee = "Platform",
                    Description = "desc",
                    CostCenter = "eng",
                });

            updated.Label.Should().Be("after");
            updated.Assignee.Should().Be("Platform");
            updated.Description.Should().Be("desc");
            updated.CostCenter.Should().Be("eng");
        }
    }

    [Fact]
    public async Task ListAsync_IncludeUsageSummary_AttachesSummaries()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference, CostCenter = "eng" });

            var billingEvents = new BillingEventRepository(db);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await billingEvents.TryAppendAsync(new BillingEventRecord(
                Guid.NewGuid(),
                "req-1",
                tenantId,
                created.Id,
                "gpt-4o",
                "eng",
                10,
                5,
                null,
                null,
                0.10m,
                100,
                DateTimeOffset.UtcNow));

            var list = await sut.ListAsync(tenantId, includeUsageSummary: true);
            var item = list.Single(x => x.Id == created.Id);
            item.UsageSummary.Should().NotBeNull();
            item.UsageSummary!.RequestCount.Should().Be(1);
            item.UsageSummary.PromptTokens.Should().Be(10);
        }
    }

    [Fact]
    public async Task GetUsageAsync_ReturnsSummaryAndEvents()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest
                {
                    Role = ApiKeyRole.Inference,
                    Label = "ops",
                    Assignee = "Team",
                    CostCenter = "eng",
                });

            var billingEvents = new BillingEventRepository(db);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await billingEvents.TryAppendAsync(new BillingEventRecord(
                Guid.NewGuid(),
                "req-usage",
                tenantId,
                created.Id,
                "gpt-4o",
                "eng",
                12,
                6,
                null,
                null,
                0.12m,
                120,
                DateTimeOffset.UtcNow));

            var usage = await sut.GetUsageAsync(tenantId, created.Id, today, today);

            usage.Id.Should().Be(created.Id);
            usage.KeyPrefix.Should().Be(created.KeyPrefix);
            usage.Label.Should().Be("ops");
            usage.Assignee.Should().Be("Team");
            usage.CostCenter.Should().Be("eng");
            usage.FromDate.Should().Be(today);
            usage.ToDate.Should().Be(today);
            usage.Summary.RequestCount.Should().Be(1);
            usage.Events.Should().ContainSingle(e => e.RequestId == "req-usage");
        }
    }

    [Fact]
    public async Task GetUsageAsync_WrongTenant_ThrowsUnauthorizedAccessException()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

            var act = () => sut.GetUsageAsync(Guid.NewGuid(), created.Id, null, null);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    [Fact]
    public async Task GetUsageAsync_MissingKey_ThrowsKeyNotFoundException()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var act = () => sut.GetUsageAsync(tenantId, Guid.NewGuid(), null, null);

            await act.Should().ThrowAsync<KeyNotFoundException>();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Lifecycle: archive, unarchive, delete
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ArchiveAsync_ActiveKey_ThrowsKeyNotRevoked()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());

            var act = () => sut.ArchiveAsync(tenantId, created.Id);

            // Archiving a live credential would hide the key an operator most needs to see.
            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("key_not_revoked");
        }
    }

    [Fact]
    public async Task ArchiveAsync_RevokedKey_HidesItFromTheDefaultListButKeepsItsUsage()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { CostCenter = "eng" });
            await AppendUsageAsync(db, tenantId, created.Id, "req-archived");

            await sut.RevokeAsync(tenantId, created.Id);
            await sut.ArchiveAsync(tenantId, created.Id);

            (await sut.ListAsync(tenantId)).Should().BeEmpty();

            var archived = (await sut.ListAsync(tenantId, includeUsageSummary: true, includeArchived: true)).Single();
            archived.IsArchived.Should().BeTrue();
            archived.IsRevoked.Should().BeTrue();
            archived.HasUsage.Should().BeTrue();
            archived.CanDelete.Should().BeFalse();
            archived.UsageSummary!.RequestCount.Should().Be(1, "archiving preserves the usage record, that is its purpose");
        }
    }

    [Fact]
    public async Task ArchiveAsync_AlreadyArchived_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            await sut.RevokeAsync(tenantId, created.Id);
            await sut.ArchiveAsync(tenantId, created.Id);

            var act = () => sut.ArchiveAsync(tenantId, created.Id);

            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("already_archived");
        }
    }

    [Fact]
    public async Task UnarchiveAsync_ReturnsKeyToTheListStillRevoked()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            await sut.RevokeAsync(tenantId, created.Id);
            await sut.ArchiveAsync(tenantId, created.Id);

            await sut.UnarchiveAsync(tenantId, created.Id);

            var item = (await sut.ListAsync(tenantId)).Single();
            item.IsArchived.Should().BeFalse();
            item.IsRevoked.Should().BeTrue("unarchiving files a key back into view, it does not revive the credential");
        }
    }

    [Fact]
    public async Task UnarchiveAsync_NotArchived_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());

            var act = () => sut.UnarchiveAsync(tenantId, created.Id);

            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("not_archived");
        }
    }

    [Fact]
    public async Task DeleteAsync_NeverUsedRevokedKey_RemovesItAndLeavesATombstone()
    {
        var (sut, tenantId, validator, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Label = "unused" });
            await sut.RevokeAsync(tenantId, created.Id);

            var deleted = await sut.DeleteAsync(tenantId, created.Id, null, created.KeyPrefix);

            deleted.KeyPrefix.Should().Be(created.KeyPrefix, "the caller needs a snapshot for the audit entry");
            deleted.Label.Should().Be("unused");
            (await sut.ListAsync(tenantId, includeArchived: true)).Should().BeEmpty();

            var history = await sut.GetLifecycleAsync(tenantId, created.Id);
            history.Exists.Should().BeFalse();
            history.Status.Should().Be("deleted");
            history.KeyPrefix.Should().Be(created.KeyPrefix);
            history.Events.Select(e => e.Event).Should().Equal(["Created", "Revoked", "Deleted"]);
        }
    }

    [Fact]
    public async Task DeleteAsync_KeyWithLastUsedAt_ThrowsKeyHasUsage()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            var usedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await new ApiKeyRepository(db).TouchLastUsedAsync(created.Id, usedAt);
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.DeleteAsync(tenantId, created.Id, null, created.KeyPrefix);

            var conflict = (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which;
            conflict.Code.Should().Be("key_has_usage");
            conflict.LastUsedAt.Should().Be(usedAt);
            (await sut.ListAsync(tenantId)).Should().ContainSingle("the key must still be there");
        }
    }

    [Fact]
    public async Task DeleteAsync_KeyWithBillingEventsButNoLastUsedAt_ThrowsKeyHasUsage()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            // A ledger row with no LastUsedAt: the two signals are independent, and either one alone
            // has to be enough to stop the delete.
            await AppendUsageAsync(db, tenantId, created.Id, "req-ledger-only");
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.DeleteAsync(tenantId, created.Id, null, created.KeyPrefix);

            var conflict = (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which;
            conflict.Code.Should().Be("key_has_usage");
            conflict.BillingEventCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task DeleteAsync_KeyWithBillingEventOutsideTheCurrentMonth_ThrowsKeyHasUsage()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            await AppendUsageAsync(db, tenantId, created.Id, "req-old", DateTimeOffset.UtcNow.AddYears(-1));
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.DeleteAsync(tenantId, created.Id, null, created.KeyPrefix);

            // The month-to-date usage summary reports nothing for this key. Deciding deletability from
            // that would destroy the ledger's only reference to it.
            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("key_has_usage");
        }
    }

    [Fact]
    public async Task DeleteAsync_KeyWithOnlyAGatewayErrorRecord_ThrowsKeyHasUsage()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            db.GatewayErrors.Add(new Persistence.Entities.GatewayErrorEntity
            {
                RecordId = "err_" + Guid.NewGuid().ToString("N"),
                Fingerprint = "fp-upstream",
                OccurredAt = DateTimeOffset.UtcNow,
                Level = "Error",
                Source = "proxy",
                Category = "upstream",
                Message = "upstream refused",
                ApiKeyId = created.Id.ToString(),
                TenantId = tenantId.ToString(),
            });
            await db.SaveChangesAsync();
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.DeleteAsync(tenantId, created.Id, null, created.KeyPrefix);

            // A key whose only trace is a failed request still left an auditable record behind.
            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("key_has_usage");
        }
    }

    [Fact]
    public async Task DeleteAsync_ActiveKey_ThrowsKeyNotRevoked()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());

            var act = () => sut.DeleteAsync(tenantId, created.Id, null, created.KeyPrefix);

            // Revoke-first closes the window in which the key could serve its first request between
            // the eligibility check and the delete.
            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("key_not_revoked");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-33pol-wrong")]
    public async Task DeleteAsync_WithoutMatchingPrefixConfirmation_Throws(string? confirmation)
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.DeleteAsync(tenantId, created.Id, null, confirmation);

            await act.Should().ThrowAsync<ArgumentException>();
            (await sut.ListAsync(tenantId)).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task DeleteAsync_OwnKey_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            // A spare admin so revoking the target does not trip the last-admin guard.
            await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            await sut.RevokeAsync(tenantId, created.Id, actorKeyId: Guid.NewGuid());

            var act = () => sut.DeleteAsync(tenantId, created.Id, created.Id, created.KeyPrefix);

            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("self_action");
        }
    }

    [Fact]
    public async Task DeleteAsync_OtherTenantsKey_ThrowsUnauthorized()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            await sut.RevokeAsync(tenantId, created.Id);

            var act = () => sut.DeleteAsync(Guid.NewGuid(), created.Id, null, created.KeyPrefix);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Guards against locking the tenant out
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RevokeAsync_OwnKey_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            var mine = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });

            var act = () => sut.RevokeAsync(tenantId, mine.Id, actorKeyId: mine.Id);

            // Unrecoverable from the console: the admin would be revoking the credential they are
            // holding the door open with.
            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("self_action");
        }
    }

    [Fact]
    public async Task RevokeAsync_LastActiveAdminKey_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var onlyAdmin = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });

            var act = () => sut.RevokeAsync(tenantId, onlyAdmin.Id, actorKeyId: Guid.NewGuid());

            (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("last_admin_key");
        }
    }

    [Fact]
    public async Task RevokeAsync_AdminKeyWithAnotherAdminRemaining_Succeeds()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var first = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Both });

            await sut.RevokeAsync(tenantId, first.Id, actorKeyId: Guid.NewGuid());

            (await sut.ListAsync(tenantId)).Single(k => k.Id == first.Id).IsRevoked.Should().BeTrue();
        }
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRevoked_IsIdempotent()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            await sut.RevokeAsync(tenantId, created.Id);

            // A client retrying after a timeout during an incident must not be told its first attempt
            // failed — and the retry must not add a second Revoked event to the history.
            var act = () => sut.RevokeAsync(tenantId, created.Id);
            await act.Should().NotThrowAsync();

            var history = await sut.GetLifecycleAsync(tenantId, created.Id);
            history.Events.Count(e => e.Event == "Revoked").Should().Be(1);
        }
    }

    [Fact]
    public async Task RevokeManyAsync_SkipsProtectedKeysInsteadOfFailingTheBatch()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var mine = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            var other = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

            var revoked = await sut.RevokeManyAsync(tenantId, [mine.Id, other.Id], actorKeyId: mine.Id);

            // One protected key must not strand the rest of the batch; the count is what reports back.
            revoked.Should().Be(1);
            var keys = await sut.ListAsync(tenantId);
            keys.Single(k => k.Id == mine.Id).IsRevoked.Should().BeFalse();
            keys.Single(k => k.Id == other.Id).IsRevoked.Should().BeTrue();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // History
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetLifecycleAsync_RecordsEveryTransitionWithItsActor()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var actor = Guid.NewGuid();
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Label = "pipeline" });
            await sut.RevokeAsync(tenantId, created.Id, actor);
            await sut.ArchiveAsync(tenantId, created.Id, actor);
            await sut.UnarchiveAsync(tenantId, created.Id, actor);

            var history = await sut.GetLifecycleAsync(tenantId, created.Id);

            history.Exists.Should().BeTrue();
            history.Status.Should().Be("revoked");
            history.Label.Should().Be("pipeline");
            history.Events.Select(e => e.Event).Should().Equal(["Created", "Revoked", "Archived", "Unarchived"]);
            history.Events.Skip(1).Should().OnlyContain(e => e.ActorApiKeyId == actor);
        }
    }

    [Fact]
    public async Task GetLifecycleAsync_OtherTenant_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());

            var act = () => sut.GetLifecycleAsync(Guid.NewGuid(), created.Id);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    [Fact]
    public async Task GetLifecycleAsync_UnknownKey_Throws()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var act = () => sut.GetLifecycleAsync(tenantId, Guid.NewGuid());

            await act.Should().ThrowAsync<KeyNotFoundException>();
        }
    }

    [Fact]
    public async Task ListAsync_ExcludesTheCallersOwnKeyFromCanDelete()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            // A third admin that stays live, so both revokes below clear the last-admin guard.
            await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            var mine = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            var theirs = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            await sut.RevokeAsync(tenantId, mine.Id, actorKeyId: theirs.Id);
            await sut.RevokeAsync(tenantId, theirs.Id, actorKeyId: mine.Id);

            var keys = await sut.ListAsync(tenantId, actorKeyId: mine.Id);

            keys.Single(k => k.Id == mine.Id).CanDelete.Should().BeFalse();
            keys.Single(k => k.Id == theirs.Id).CanDelete.Should().BeTrue();
        }
    }

    [Fact]
    public async Task ListAsync_ActiveKeyWithLedgerUsage_ReportsHasUsage()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest());
            // A ledger row with no LastUsedAt, on a key that is still live.
            await AppendUsageAsync(db, tenantId, created.Id, "req-active");

            var item = (await sut.ListAsync(tenantId)).Single(k => k.Id == created.Id);

            item.HasUsage.Should().BeTrue("the field claims to report any billing or error record");
            item.CanDelete.Should().BeFalse("a live key is not deletable regardless");
        }
    }

    /// <summary>
    /// The list endpoint decides <c>canDelete</c> with a batched usage probe while the delete endpoint
    /// uses the single-key one. If the two ever disagree the console offers a delete that then fails,
    /// so this pins them together across every signal.
    /// </summary>
    [Fact]
    public async Task ListAsync_CanDelete_AgreesWithWhatDeleteAsyncActuallyAllows()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var never = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Label = "never" });
            var byLastUsed = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Label = "lastUsed" });
            var byLedger = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Label = "ledger" });
            var byError = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Label = "error" });

            await new ApiKeyRepository(db).TouchLastUsedAsync(byLastUsed.Id, DateTimeOffset.UtcNow);
            await AppendUsageAsync(db, tenantId, byLedger.Id, "req-agree");
            db.GatewayErrors.Add(new Persistence.Entities.GatewayErrorEntity
            {
                RecordId = "err_" + Guid.NewGuid().ToString("N"),
                Fingerprint = "fp",
                OccurredAt = DateTimeOffset.UtcNow,
                Level = "Error",
                Source = "proxy",
                Category = "upstream",
                Message = "boom",
                ApiKeyId = byError.Id.ToString(),
                TenantId = tenantId.ToString(),
            });
            await db.SaveChangesAsync();

            foreach (var id in new[] { never.Id, byLastUsed.Id, byLedger.Id, byError.Id })
            {
                await sut.RevokeAsync(tenantId, id);
            }

            var keys = (await sut.ListAsync(tenantId)).ToDictionary(k => k.Id);

            keys[never.Id].CanDelete.Should().BeTrue();
            keys[never.Id].HasUsage.Should().BeFalse();
            foreach (var used in new[] { byLastUsed.Id, byLedger.Id, byError.Id })
            {
                keys[used].HasUsage.Should().BeTrue();
                keys[used].CanDelete.Should().BeFalse();
            }

            // And the endpoint agrees, in both directions.
            foreach (var used in new[] { byLastUsed.Id, byLedger.Id, byError.Id })
            {
                var prefix = keys[used].KeyPrefix;
                var act = () => sut.DeleteAsync(tenantId, used, null, prefix);
                (await act.Should().ThrowAsync<ApiKeyLifecycleException>()).Which.Code.Should().Be("key_has_usage");
            }

            await sut.DeleteAsync(tenantId, never.Id, null, keys[never.Id].KeyPrefix);
            (await sut.ListAsync(tenantId, includeArchived: true)).Should().NotContain(k => k.Id == never.Id);
        }
    }

    [Fact]
    public async Task UpdateAsync_ReportsUsageTheSameWayTheListingDoes()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var created = await sut.CreateAsync(
                tenantId,
                new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference, Label = "before" });
            await AppendUsageAsync(db, tenantId, created.Id, "req-update-usage");

            var updated = await sut.UpdateAsync(
                tenantId,
                created.Id,
                new UpdateAdminApiKeyRequest { Label = "after" });

            // The same DTO the listing returns, so it has to agree with it. Reporting a used key as
            // never-used would tell an API client the key is a deletion candidate.
            updated.HasUsage.Should().BeTrue();
            updated.HasUsage.Should().Be(
                (await sut.ListAsync(tenantId)).Single(k => k.Id == created.Id).HasUsage);

            // Still false, and correctly so: the key is not revoked, so it is not deletable.
            updated.CanDelete.Should().BeFalse();
        }
    }

    /// <summary>
    /// The last-admin guard reads the count on one connection and writes on another, so two admins
    /// revoking the last two admin keys at once can both pass the pre-flight check. The recheck after
    /// the write is what actually decides it.
    /// </summary>
    [Fact]
    public async Task RevokeAsync_WhenAnotherRequestTookTheLastAdminKeyConcurrently_RevertsAndThrows()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new GatewayDbContext(options);

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Persistence.Entities.TenantEntity
        {
            Id = tenantId,
            Slug = "t1",
            Name = "Tenant",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var real = new ApiKeyRepository(db);
        // Answers the pre-flight check as though a second admin key were still active, then tells the
        // truth — exactly what the losing side of the race observes.
        var apiKeys = new StaleAdminCountApiKeyRepository(real, firstAnswer: 2);
        var securityOptions = Options.Create(new GatewaySecurityOptions { KeyPepper = Pepper });
        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var validator = new ApiKeyValidator(apiKeys, new TenantRepository(db), memoryCache, securityOptions);
        var sut = new AdminKeyService(
            apiKeys,
            validator,
            new BillingEventRepository(db),
            new ApiKeyLifecycleEventRepository(db),
            securityOptions,
            new GatewayErrorRepository(db));

        var onlyAdmin = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });

        var act = () => sut.RevokeAsync(tenantId, onlyAdmin.Id);

        (await act.Should().ThrowAsync<ApiKeyLifecycleException>())
            .Which.Code.Should().Be("last_admin_key");

        // Reverted, not left half-applied: the tenant keeps its way in.
        var after = (await sut.ListAsync(tenantId)).Single(k => k.Id == onlyAdmin.Id);
        after.IsRevoked.Should().BeFalse();

        // And no Revoked event was written for a revocation that did not stand.
        var lifecycle = await sut.GetLifecycleAsync(tenantId, onlyAdmin.Id);
        lifecycle.Events.Should().NotContain(e => e.Event == "Revoked");
    }

    [Fact]
    public async Task RevokeManyAsync_StopsAtTheLastAdminKeyWithoutFailingTheBatch()
    {
        var (sut, tenantId, _, db) = await CreateSutAsync();
        await using (db)
        {
            var adminA = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            var adminB = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Admin });
            var inference = await sut.CreateAsync(tenantId, new CreateAdminApiKeyRequest { Role = ApiKeyRole.Inference });

            // Both admin keys plus an ordinary one, in a single batch. The second admin key is the
            // tenant's last, and the batch must skip it rather than abort on it.
            var revoked = await sut.RevokeManyAsync(tenantId, [adminA.Id, adminB.Id, inference.Id]);

            revoked.Should().Be(2);

            var keys = (await sut.ListAsync(tenantId)).ToDictionary(k => k.Id);
            keys[inference.Id].IsRevoked.Should().BeTrue("an unprotected key in the batch still goes");
            new[] { keys[adminA.Id].IsRevoked, keys[adminB.Id].IsRevoked }
                .Should().ContainSingle(r => r, "exactly one admin key survives, whichever came second");
        }
    }

    private sealed class StaleAdminCountApiKeyRepository(IApiKeyRepository inner, int firstAnswer)
        : IApiKeyRepository
    {
        private bool _answered;

        public Task<int> CountActiveAdminKeysAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            if (_answered)
            {
                return inner.CountActiveAdminKeysAsync(tenantId, cancellationToken);
            }

            _answered = true;
            return Task.FromResult(firstAnswer);
        }

        public Task<ApiKeyRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<ApiKeyRecord>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
            inner.GetByIdsAsync(ids, cancellationToken);

        public Task<ApiKeyRecord?> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
            inner.FindByPrefixAsync(keyPrefix, cancellationToken);

        public Task<IReadOnlyList<ApiKeyRecord>> FindByPrefixesAsync(IReadOnlyCollection<string> keyPrefixes, CancellationToken cancellationToken = default) =>
            inner.FindByPrefixesAsync(keyPrefixes, cancellationToken);

        public Task<IReadOnlyList<ApiKeyRecord>> ListByTenantAsync(Guid tenantId, bool includeArchived = false, CancellationToken cancellationToken = default) =>
            inner.ListByTenantAsync(tenantId, includeArchived, cancellationToken);

        public Task<ApiKeyRecord> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(apiKey, cancellationToken);

        public Task RevokeAsync(Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(id, revokedAt, cancellationToken);

        public Task RestoreRevokedAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.RestoreRevokedAsync(id, cancellationToken);

        public Task ArchiveAsync(Guid id, DateTimeOffset archivedAt, CancellationToken cancellationToken = default) =>
            inner.ArchiveAsync(id, archivedAt, cancellationToken);

        public Task UnarchiveAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.UnarchiveAsync(id, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);

        public Task<ApiKeyRecord> UpdateMetadataAsync(Guid id, ApiKeyMetadataUpdate update, CancellationToken cancellationToken = default) =>
            inner.UpdateMetadataAsync(id, update, cancellationToken);

        public Task TouchLastUsedAsync(Guid id, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            inner.TouchLastUsedAsync(id, atUtc, cancellationToken);

        public Task<IReadOnlyList<ApiKeyRecord>> ListExpiringAsync(DateTimeOffset before, CancellationToken cancellationToken = default) =>
            inner.ListExpiringAsync(before, cancellationToken);

        public Task<IReadOnlyList<ApiKeyRecord>> ListIdleAsync(DateTimeOffset idleSince, CancellationToken cancellationToken = default) =>
            inner.ListIdleAsync(idleSince, cancellationToken);

        public Task<(int Total, int Revoked, int Archived)> CountAsync(CancellationToken cancellationToken = default) =>
            inner.CountAsync(cancellationToken);
    }

    private static Task AppendUsageAsync(
        GatewayDbContext db,
        Guid tenantId,
        Guid keyId,
        string requestId,
        DateTimeOffset? at = null) =>
        new BillingEventRepository(db).TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(),
            requestId,
            tenantId,
            keyId,
            "gpt-4o",
            "eng",
            10,
            5,
            null,
            null,
            0.10m,
            100,
            at ?? DateTimeOffset.UtcNow));

    private static async Task<(AdminKeyService Sut, Guid TenantId, ApiKeyValidator Validator, GatewayDbContext Db)> CreateSutAsync()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new GatewayDbContext(options);

        var tenantId = Guid.NewGuid();
        db.Tenants.Add(new Persistence.Entities.TenantEntity
        {
            Id = tenantId,
            Slug = "t1",
            Name = "Tenant",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var apiKeys = new ApiKeyRepository(db);
        var tenants = new TenantRepository(db);
        var securityOptions = Options.Create(new GatewaySecurityOptions { KeyPepper = Pepper });
        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var validator = new ApiKeyValidator(apiKeys, tenants, memoryCache, securityOptions);
        var billingEvents = new BillingEventRepository(db);
        var lifecycle = new ApiKeyLifecycleEventRepository(db);
        var gatewayErrors = new GatewayErrorRepository(db);
        var sut = new AdminKeyService(apiKeys, validator, billingEvents, lifecycle, securityOptions, gatewayErrors);
        return (sut, tenantId, validator, db);
    }
}
