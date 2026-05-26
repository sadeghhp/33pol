namespace Pol33.Core.Configuration;

public sealed class GatewayTlsOptions
{
    public const string SectionName = "Tls";

    public bool ValidateUpstreamCertificates { get; set; } = true;
}
