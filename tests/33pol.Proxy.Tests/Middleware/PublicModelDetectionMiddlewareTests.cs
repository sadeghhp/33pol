using System.Text;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Proxy.Middleware;

namespace Pol33.Proxy.Tests.Middleware;

public sealed class PublicModelDetectionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_PublicModel_SetsContextItemsAndRewindsBody()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("alias", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig
                {
                    Id = "canonical",
                    Url = "http://backend",
                    PublicAccess = true,
                    Aliases = ["alias"],
                };
                return true;
            });

        var nextCalled = false;
        var middleware = new PublicModelDetectionMiddleware(
            ctx =>
            {
                nextCalled = true;
                ctx.Request.Body.Position.Should().Be(0);
                return Task.CompletedTask;
            },
            registry);

        var context = CreatePostContext("""{"model":"alias","stream":false}""");

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Items[PublicModelAccessKeys.IsPublicInference].Should().Be(true);
        context.Items[PublicModelAccessKeys.CanonicalModelId].Should().Be("canonical");
    }

    [Fact]
    public async Task InvokeAsync_PrivateModel_DoesNotSetItems()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.TryGetModel("m1", out Arg.Any<ModelConfig?>())
            .Returns(call =>
            {
                call[1] = new ModelConfig { Id = "m1", Url = "http://backend" };
                return true;
            });

        var middleware = new PublicModelDetectionMiddleware(_ => Task.CompletedTask, registry);
        var context = CreatePostContext("""{"model":"m1"}""");

        await middleware.InvokeAsync(context);

        context.Items.ContainsKey(PublicModelAccessKeys.IsPublicInference).Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_GetRequest_SkipsDetection()
    {
        var registry = Substitute.For<IModelRegistry>();
        var middleware = new PublicModelDetectionMiddleware(_ => Task.CompletedTask, registry);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/chat/completions";

        await middleware.InvokeAsync(context);

        await registry.DidNotReceive().TryGetModel(Arg.Any<string>(), out Arg.Any<ModelConfig?>());
    }

    private static DefaultHttpContext CreatePostContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        return context;
    }
}
