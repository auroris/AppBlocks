using Microsoft.AspNetCore.Authorization;
using AppBlocks.Blocks;

namespace AppBlocks.Tests;

public sealed class FormsTests
{
    [Fact]
    public async Task Page_policies_preserve_the_hosts_existing_policy_provider()
    {
        var original = new TestPolicyProvider();
        var wrapper = new AppPagePolicyProvider(original);
        Assert.Same(original.Policy, await wrapper.GetPolicyAsync("existing-dynamic-policy"));
        Assert.Same(original.Policy, await wrapper.GetDefaultPolicyAsync());
        var page = Guid.NewGuid();
        var combined = await wrapper.GetPolicyAsync(AppPagePolicyProvider.Name(page, "existing-dynamic-policy"));
        Assert.Contains(combined!.Requirements, r => r is AppPageRequirement p && p.PageKey == page);
        Assert.All(original.Policy.Requirements, r => Assert.Contains(r, combined.Requirements));
    }

    private sealed class TestPolicyProvider : IAuthorizationPolicyProvider
    {
        public AuthorizationPolicy Policy { get; } = new AuthorizationPolicyBuilder().RequireClaim("host-permission").Build();
        public Task<AuthorizationPolicy?> GetPolicyAsync(string name) => Task.FromResult<AuthorizationPolicy?>(name == "existing-dynamic-policy" ? Policy : null);
        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(Policy);
        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);
    }

    [Fact]
    public void Instance_names_distinguish_copied_pages_and_blocks()
    {
        var page = Guid.NewGuid(); var block = Guid.NewGuid();
        var name = AppBlockContent.ReportName(page, block);
        Assert.True(AppBlockContent.ParseReportName(name, out var parsedPage, out var parsedBlock));
        Assert.Equal(page, parsedPage); Assert.Equal(block, parsedBlock);
        Assert.NotEqual(name, AppBlockContent.ReportName(Guid.NewGuid(), block));
        Assert.False(AppBlockContent.ParseReportName("page-orders", out _, out _));
    }
}
