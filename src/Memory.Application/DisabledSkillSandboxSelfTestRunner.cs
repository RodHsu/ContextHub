namespace Memory.Application;

public sealed class DisabledSkillSandboxSelfTestRunner : ISkillSandboxSelfTestRunner
{
    public Task<SkillSandboxSelfTestResult> RunAsync(SkillSandboxSelfTestRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SkillSandboxSelfTestResult(
            false,
            false,
            string.Empty,
            "SandboxUnavailable",
            "The isolated Skill sandbox provider is not configured.",
            0));
    }
}
