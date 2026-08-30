using FluentAssertions;
using Memory.ChatGptGateway;
using Microsoft.AspNetCore.Http;

namespace Memory.ChatGptGatewayTests;

public sealed class GatewayPublicPathsTests
{
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp-chat")]
    [InlineData("/.well-known/oauth-protected-resource/mcp-automation")]
    [InlineData("/.well-known/oauth-authorization-server/mcp-chat")]
    [InlineData("/.well-known/oauth-authorization-server/mcp-automation")]
    [InlineData("/.well-known/openid-configuration/mcp-chat")]
    [InlineData("/.well-known/openid-configuration/mcp-automation")]
    public void Automation_And_General_Metadata_Should_Be_Public_Bootstrap_Paths(string path)
        => GatewayPublicPaths.IsActorBootstrapPath(new PathString(path)).Should().BeTrue();

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/unknown")]
    [InlineData("/.well-known/oauth-authorization-server/mcp-automation/extra")]
    [InlineData("/.well-known/openid-configuration/anything")]
    public void Unknown_Metadata_Resources_Should_Remain_Protected(string path)
        => GatewayPublicPaths.IsActorBootstrapPath(new PathString(path)).Should().BeFalse();
}
