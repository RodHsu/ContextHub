using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class ConversationInsightClassifierTests
{
    [Fact]
    public void Extract_Should_Create_PreferenceCandidate_From_UserSummary()
    {
        var checkpoint = CreateCheckpoint(
            userMessageSummary: "使用者偏好回覆預設使用繁體中文，技術名詞保留英文。",
            agentMessageSummary: string.Empty,
            sessionSummary: string.Empty);

        var results = ConversationInsightClassifier.Extract(checkpoint, [], 240);

        results.Should().ContainSingle(x => x.InsightType == ConversationInsightType.PreferenceCandidate);
    }

    [Fact]
    public void Extract_Should_Create_Decision_From_AgentSummary()
    {
        var checkpoint = CreateCheckpoint(
            userMessageSummary: string.Empty,
            agentMessageSummary: "系統決定採用 shared summary layer 作為跨專案共用知識入口。",
            sessionSummary: string.Empty);

        var results = ConversationInsightClassifier.Extract(checkpoint, [], 240);

        results.Should().ContainSingle(x => x.InsightType == ConversationInsightType.Decision);
    }

    [Fact]
    public void Extract_Should_Create_Episode_From_ToolCall()
    {
        var checkpoint = CreateCheckpoint(
            userMessageSummary: string.Empty,
            agentMessageSummary: string.Empty,
            sessionSummary: string.Empty);
        var toolCalls = new[]
        {
            new ConversationToolCallRequest(
                "memory_upsert",
                "寫入專案摘要",
                "完成寫入",
                true)
        };

        var results = ConversationInsightClassifier.Extract(checkpoint, toolCalls, 240);

        results.Should().ContainSingle(x => x.InsightType == ConversationInsightType.Episode);
    }

    private static ConversationCheckpoint CreateCheckpoint(
        string userMessageSummary,
        string agentMessageSummary,
        string sessionSummary)
        => new()
        {
            ProjectId = ProjectContext.DefaultProjectId,
            ProjectName = "ContextHub",
            SourceSystem = "codex",
            SourceKind = ConversationSourceKind.HostEvent,
            EventType = ConversationEventType.TurnCompleted,
            SourceRef = "unit-tests",
            UserMessageSummary = userMessageSummary,
            AgentMessageSummary = agentMessageSummary,
            SessionSummary = sessionSummary
        };
}
