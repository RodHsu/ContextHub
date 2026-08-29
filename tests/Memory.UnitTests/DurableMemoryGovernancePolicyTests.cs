using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class DurableMemoryGovernancePolicyTests
{
    [Fact]
    public void Empty_Project_Information_Is_System_Metadata_And_Not_Retrieval_Eligible()
    {
        var memory = new MemoryItem
        {
            ExternalKey = DurableMemoryGovernancePolicy.ProjectInformationExternalKey,
            MemoryType = MemoryType.Artifact,
            Content = string.Empty,
            Summary = string.Empty
        };

        DurableMemoryGovernancePolicy.IsSystemProjectMetadata(memory).Should().BeTrue();
        DurableMemoryGovernancePolicy.RequiresKnowledgeBody(memory).Should().BeFalse();
        DurableMemoryGovernancePolicy.IsRetrievalEligible(memory).Should().BeFalse();
    }

    [Fact]
    public void Ordinary_Durable_Memory_Still_Requires_A_Knowledge_Body()
    {
        var memory = new MemoryItem
        {
            ExternalKey = "ordinary-artifact",
            MemoryType = MemoryType.Artifact,
            Content = string.Empty,
            Summary = string.Empty
        };

        DurableMemoryGovernancePolicy.IsSystemProjectMetadata(memory).Should().BeFalse();
        DurableMemoryGovernancePolicy.RequiresKnowledgeBody(memory).Should().BeTrue();
        DurableMemoryGovernancePolicy.IsRetrievalEligible(memory).Should().BeFalse();
    }
}
