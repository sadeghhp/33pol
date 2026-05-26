using Serilog;
using Serilog.Events;
namespace Pol33.App.Hosting;

public static class GatewaySerilogExtensions
{
    public const string RequestLogMessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

    public static WebApplicationBuilder AddGatewaySerilog(this WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "33pol"));

        return builder;
    }

    public static WebApplication UseGatewaySerilogRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = RequestLogMessageTemplate;
        });

        return app;
    }
}
