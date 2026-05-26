namespace Pol33.Core.Configuration;

public sealed class QuotaOptions
{
    public const string SectionName = "Quota";

    public long DefaultMonthlyTokenLimit { get; set; } = 1_000_000;

    public double SoftLimitRatio { get; set; } = 0.9;
}
