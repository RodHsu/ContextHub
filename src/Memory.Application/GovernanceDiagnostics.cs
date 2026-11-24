using System.Diagnostics;

namespace Memory.Application;

internal static class GovernanceDiagnostics
{
    internal static readonly ActivitySource Activities = new("ContextHub.Governance");
}
