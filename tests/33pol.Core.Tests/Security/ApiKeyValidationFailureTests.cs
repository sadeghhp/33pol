using FluentAssertions;
using Pol33.Core.Security;

namespace Pol33.Core.Tests.Security;

/// <summary>
/// Pins which failure reasons count as a credential the gateway issued.
/// </summary>
/// <remarks>
/// This predicate decides whether an unusable key may be ignored on the anonymous-capable routes,
/// so misplacing a reason has consequences in both directions: putting a real revoked key on the
/// unrecognised side would serve its holder 200s forever, and putting an unknown key on the
/// recognised side would 401 every SDK client that sends a placeholder token to a public model.
/// The exhaustive case below is deliberate — a new reason should fail this test until it has been
/// classified on purpose.
/// </remarks>
public sealed class ApiKeyValidationFailureTests
{
    [Theory]
    [InlineData(ApiKeyValidationFailure.Missing, false)]
    [InlineData(ApiKeyValidationFailure.Invalid, false)]
    [InlineData(ApiKeyValidationFailure.Expired, true)]
    [InlineData(ApiKeyValidationFailure.Revoked, true)]
    [InlineData(ApiKeyValidationFailure.TenantInactive, true)]
    public void IsRecognizedCredential_ClassifiesEachFailure(ApiKeyValidationFailure failure, bool expected)
    {
        failure.IsRecognizedCredential().Should().Be(expected);
    }

    [Fact]
    public void IsRecognizedCredential_CoversEveryDefinedFailure()
    {
        var classified = new[]
        {
            ApiKeyValidationFailure.Missing,
            ApiKeyValidationFailure.Invalid,
            ApiKeyValidationFailure.Expired,
            ApiKeyValidationFailure.Revoked,
            ApiKeyValidationFailure.TenantInactive,
        };

        Enum.GetValues<ApiKeyValidationFailure>().Should().BeEquivalentTo(classified);
    }
}
