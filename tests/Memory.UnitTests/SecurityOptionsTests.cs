using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class SecurityOptionsTests
{
    [Fact]
    public void ContextHub_Security_Should_Require_Authentication_By_Default()
    {
        var options = new ContextHubSecurityOptions();

        options.RequireAuthentication.Should().BeTrue();
    }
}
