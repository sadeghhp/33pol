namespace Pol33.App.Hosting;

public static class GatewayKestrelExtensions
{
    public static WebApplicationBuilder ConfigureGatewayKestrel(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AllowSynchronousIO = false;
            options.AddServerHeader = false;
            options.Limits.MaxResponseBufferSize = null;
        });

        return builder;
    }
}
