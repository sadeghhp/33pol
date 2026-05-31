using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Core.Tests.Usage;

public sealed class AdminBillingEventMapperTests
{
    [Fact]
    public void ToListItem_WithLookup_IncludesKeyPrefixAndAssignee()
    {
        var keyId = Guid.NewGuid();
        var record = new BillingEventRecord(
            Guid.NewGuid(),
            "req-1",
            Guid.NewGuid(),
            keyId,
            "gpt-4o",
            "eng",
            10,
            5,
            null,
            null,
            0.01m,
            100,
            DateTimeOffset.UtcNow);

        var item = AdminBillingEventMapper.ToListItem(
            record,
            new Dictionary<Guid, AdminBillingEventMapper.ApiKeyLookup>
            {
                [keyId] = new("sk-prefix", "Platform"),
            });

        item.KeyPrefix.Should().Be("sk-prefix");
        item.Assignee.Should().Be("Platform");
        item.ModelId.Should().Be("gpt-4o");
    }
}
