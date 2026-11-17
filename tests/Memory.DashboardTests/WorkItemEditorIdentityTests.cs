using System.Security.Claims;
using FluentAssertions;
using Memory.Dashboard.Services;

namespace Memory.DashboardTests;

public sealed class WorkItemEditorIdentityTests
{
    [Fact]
    public void Anonymous_principal_never_supplies_an_edit_binding_identity()
    {
        WorkItemEditorIdentity.SubjectKey(null).Should().BeEmpty();
        WorkItemEditorIdentity.SubjectKey(new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, "synthetic")]))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("bad", null)]
    [InlineData(null, "bad")]
    [InlineData("00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-000000000001")]
    public void Partial_or_invalid_tenant_identity_does_not_fall_back_to_a_legacy_name(string? tenant, string? actor)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "synthetic") };
        if (tenant is not null) claims.Add(new("contexthub:tenant_id", tenant));
        if (actor is not null) claims.Add(new("contexthub:user_id", actor));
        WorkItemEditorIdentity.SubjectKey(new(new ClaimsIdentity(claims, "synthetic-cookie"))).Should().BeEmpty();
    }

    [Fact]
    public void Tenant_and_actor_both_bind_the_ui_identity_without_using_display_name_or_credentials()
    {
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        ClaimsPrincipal Principal(Guid t, Guid a) => new(new ClaimsIdentity([new("contexthub:tenant_id", t.ToString()),
            new("contexthub:user_id", a.ToString()), new(ClaimTypes.Name, "display")], "synthetic-cookie"));
        var key = WorkItemEditorIdentity.SubjectKey(Principal(tenant, actor));
        key.Should().Be($"tenant:{tenant:N}:actor:{actor:N}");
        key.Should().NotBe(WorkItemEditorIdentity.SubjectKey(Principal(Guid.NewGuid(), actor)));
        key.Should().NotBe(WorkItemEditorIdentity.SubjectKey(Principal(tenant, Guid.NewGuid())));
    }

    [Fact]
    public void Existing_legacy_cookie_name_has_a_distinct_non_secret_identity_kind()
    {
        ClaimsPrincipal Principal(string name, string type) => new(new ClaimsIdentity([new(ClaimTypes.Name, name)], type));
        var key = WorkItemEditorIdentity.SubjectKey(Principal("synthetic", "cookie"));
        key.Should().StartWith("legacy:").And.NotContain("synthetic");
        key.Should().Be(WorkItemEditorIdentity.SubjectKey(Principal("synthetic", "cookie")));
        key.Should().NotBe(WorkItemEditorIdentity.SubjectKey(Principal("other", "cookie")));
        key.Should().NotBe(WorkItemEditorIdentity.SubjectKey(Principal("synthetic", "other-cookie")));
    }
}
