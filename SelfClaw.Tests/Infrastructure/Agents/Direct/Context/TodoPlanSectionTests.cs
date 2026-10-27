using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// P4: the plan snapshot is injected lazily. A turn without compaction must keep its system text
/// byte-identical, and both compaction points must attach the plan only once a summary covers it.
/// </summary>
public sealed class TodoPlanSectionTests
{
    private const string AgentInstructions = "follow the project instructions";

    private const string Summary = "earlier decisions";

    private const string Heading = "[SelfClaw Plan]";

    [Fact]
    public void A_turn_without_compaction_keeps_the_old_system_text_byte_for_byte()
    {
        var composer = new DirectPromptComposer();
        var systemInstructions = new[] { "capability instructions" };

        var withoutPlan = composer.BuildSystemText(AgentInstructions, systemInstructions, Interactive(), compression: null);
        var withExplicitNull = composer.BuildSystemText(AgentInstructions, systemInstructions, Interactive(),
            compression: null, todoSection: null);

        withExplicitNull.Should().Be(withoutPlan);
        withExplicitNull.Should().NotContain(Heading);
    }

    [Fact]
    public void A_blank_section_leaves_no_separator_behind()
    {
        var composer = new DirectPromptComposer();
        var systemInstructions = new[] { "capability instructions" };

        var withoutSection = composer.BuildSystemText(AgentInstructions, systemInstructions, Interactive(), compression: null);
        var withBlankSection = composer.BuildSystemText(AgentInstructions, systemInstructions, Interactive(),
            compression: null, todoSection: "  \n ");

        withBlankSection.Should().Be(withoutSection);
    }

    [Fact]
    public void A_fully_completed_plan_produces_no_section()
    {
        var plan = new TodoPlan(
        [
            new TodoItem("检查 Direct 工具装配", TodoItemStatus.Completed),
            new TodoItem("投影到 transcript 卡片", TodoItemStatus.Completed)
        ]);

        TodoPlanSection.Build(plan).Should().BeNull("there is nothing left to continue");
    }

    [Fact]
    public void An_empty_plan_produces_no_section()
    {
        TodoPlanSection.Build(new TodoPlan([])).Should().BeNull();
    }

    [Fact]
    public void The_section_marks_every_state_and_keeps_the_plan_order()
    {
        var plan = new TodoPlan(
        [
            new TodoItem("检查 Direct 工具装配", TodoItemStatus.Completed),
            new TodoItem("新增 todo_write 工具", TodoItemStatus.InProgress),
            new TodoItem("投影到 transcript 卡片", TodoItemStatus.Pending)
        ]);

        var section = TodoPlanSection.Build(plan);

        section.Should().Be(
            $"{Heading}\n" +
            "The task plan as it stood when the history above was summarized. A newer todo_write in the history supersedes it.\n" +
            "- [x] 检查 Direct 工具装配\n" +
            "- [~] 新增 todo_write 工具\n" +
            "- [ ] 投影到 transcript 卡片");
    }

    [Fact]
    public void The_newest_todo_call_wins()
    {
        var messages = new[]
        {
            TodoCall(PlanArguments(("first", "pending"))),
            TodoCall(PlanArguments(("second", "in_progress")))
        };

        var plan = TodoPlanSection.FindNewestPlan(messages);

        Assert.NotNull(plan);
        plan.Items.Should().ContainSingle().Which.Content.Should().Be("second");
    }

    [Fact]
    public void An_elided_snapshot_falls_back_to_the_previous_plan()
    {
        // Replay elision rewrites every superseded call's arguments to this placeholder, so the newest
        // readable call of a replayed history is usually not the last one the model made.
        var messages = new[]
        {
            TodoCall(PlanArguments(("survives", "in_progress"))),
            TodoCall(new Dictionary<string, object?> { ["elided"] = true })
        };

        var plan = TodoPlanSection.FindNewestPlan(messages);

        Assert.NotNull(plan);
        plan.Items.Should().ContainSingle().Which.Content.Should().Be("survives");
    }

    [Fact]
    public void A_history_without_a_todo_call_has_no_plan()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "list the files"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_file",
                new Dictionary<string, object?> { ["relativePath"] = "README.md" })])
        };

        TodoPlanSection.FindNewestPlan(messages).Should().BeNull();
    }

    [Fact]
    public void The_section_sits_between_the_summary_and_the_capability_sections()
    {
        var systemText = new DirectPromptComposer().BuildSystemText(
            AgentInstructions,
            ["first capability", "second capability"],
            Interactive(),
            CreateCompaction(throughSequence: 3),
            TodoPlanSection.Build(SamplePlan()));

        Assert.NotNull(systemText);
        var summaryIndex = systemText.IndexOf("<selfclaw-context-summary", StringComparison.Ordinal);
        var planIndex = systemText.IndexOf(Heading, StringComparison.Ordinal);
        var capabilityIndex = systemText.IndexOf("first capability", StringComparison.Ordinal);
        summaryIndex.Should().BePositive();
        planIndex.Should().BeGreaterThan(summaryIndex);
        capabilityIndex.Should().BeGreaterThan(planIndex);
    }

    [Fact]
    public void Turn_start_injects_the_plan_only_when_the_boundary_covers_it()
    {
        var snapshot = new TodoPlanSnapshot(SamplePlan(), CreateRun(), SourceSequence: 5);

        DirectAgentChatRuntime.TodoSection(snapshot, CreateCompaction(throughSequence: 5)).Should().Be(
            TodoPlanSection.Build(SamplePlan()), "the boundary includes the message that carried the plan");
        DirectAgentChatRuntime.TodoSection(snapshot, CreateCompaction(throughSequence: 4)).Should().BeNull(
            "the plan is still replayed from the history");
        DirectAgentChatRuntime.TodoSection(snapshot, compression: null).Should().BeNull("nothing was summarized");
        DirectAgentChatRuntime.TodoSection(plan: null, CreateCompaction(throughSequence: 9)).Should().BeNull();
    }

    [Fact]
    public void In_turn_compaction_that_covers_the_plan_attaches_it()
    {
        var context = new DirectConversationContext(
            AgentInstructions,
            [TodoUnit(1), TextUnit(2, "newest")],
            []);

        context.ApplyInTurnCompaction("first summary", coveredUnitCount: 1);

        var system = context.Messages[0];
        system.Role.Should().Be(ChatRole.System);
        system.Text.Should().Contain(Heading);
        system.Text.IndexOf(Heading, StringComparison.Ordinal).Should().BeGreaterThan(
            system.Text.IndexOf("<selfclaw-context-summary", StringComparison.Ordinal),
            "the snapshot follows the summary it belongs to");
    }

    [Fact]
    public void A_plan_covered_by_a_second_in_turn_compaction_does_not_stack_a_duplicate()
    {
        var context = new DirectConversationContext(AgentInstructions, [TodoUnit(1)], []);
        context.ApplyInTurnCompaction("first summary", coveredUnitCount: 1);
        // The model resubmits the unchanged list, and the next compaction covers that copy too: the snapshot is
        // already in the system text, so a second, identical section would only cost tokens.
        context.AppendResponse(PlanResponse("call-2"), TodoBindings());

        context.ApplyInTurnCompaction("second summary", coveredUnitCount: 1);

        var text = context.Messages[0].Text ?? string.Empty;
        text.Should().Contain(Heading);
        text.Split(Heading).Should().HaveCount(2, "the snapshot appears exactly once");
        text.Should().NotContain("first summary", "the second summary replaces the first in place");
    }

    [Fact]
    public void An_in_turn_snapshot_already_in_the_turn_start_system_text_is_not_repeated()
    {
        var planSection = TodoPlanSection.Build(SamplePlan());
        var systemText = new DirectPromptComposer().BuildSystemText(
            AgentInstructions, [], Interactive(), CreateCompaction(throughSequence: 1), planSection);
        var context = new DirectConversationContext(systemText, [SamePlanUnit(2)], []);

        context.ApplyInTurnCompaction("in-turn summary", coveredUnitCount: 1);

        var text = context.Messages[0].Text ?? string.Empty;
        text.Should().Contain("<selfclaw-context-summary");
        text.Should().Contain(Heading);
        text.Split(Heading).Should().HaveCount(2, "the same snapshot must not be paid for twice");
    }

    [Fact]
    public void In_turn_compaction_that_leaves_the_plan_in_the_history_attaches_nothing()
    {
        var context = new DirectConversationContext(
            AgentInstructions,
            [TextUnit(1, "oldest"), TextUnit(2, "older"), TodoUnit(3)],
            []);

        context.ApplyInTurnCompaction("first summary", coveredUnitCount: 2);

        var text = context.Messages[0].Text ?? string.Empty;
        text.Should().StartWith(AgentInstructions);
        text.Should().Contain("<selfclaw-context-summary");
        text.Should().NotContain(Heading,
            "the plan still replays from the retained unit, so a second copy would only cost tokens");
        context.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Should().ContainSingle().Which.Name.Should().Be(TodoPlanRules.ToolName,
                "the plan still replays as a tool call");
    }

    private static TodoPlan SamplePlan() => new(
    [
        new TodoItem("检查 Direct 工具装配", TodoItemStatus.Completed),
        new TodoItem("新增 todo_write 工具", TodoItemStatus.InProgress)
    ]);

    private static Dictionary<string, object?> PlanArguments(params (string Content, string Status)[] items)
        => new()
        {
            ["todos"] = items
                .Select(item => new Dictionary<string, object?>
                {
                    ["content"] = item.Content,
                    ["status"] = item.Status
                })
                .ToArray()
        };

    private static ChatMessage TodoCall(IDictionary<string, object?> arguments)
        => new(ChatRole.Assistant,
            [new FunctionCallContent($"call-{Guid.NewGuid():D}", TodoPlanRules.ToolName, arguments)]);

    private static DirectPromptHistoryUnit TodoUnit(long sequence)
        => new([TodoCall(PlanArguments(("新增 todo_write 工具", "in_progress")))], sequence);

    private static DirectPromptHistoryUnit SamePlanUnit(long sequence)
        => new([TodoCall(PlanArguments(
            ("检查 Direct 工具装配", "completed"),
            ("新增 todo_write 工具", "in_progress")))], sequence);

    private static DirectPromptHistoryUnit TextUnit(long sequence, string text)
        => new([new ChatMessage(ChatRole.Assistant, text)], sequence);

    private static ChatResponse PlanResponse(string callId)
        => new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, TodoPlanRules.ToolName,
                PlanArguments(("新增 todo_write 工具", "in_progress")))]));

    private static IReadOnlyDictionary<string, DirectToolBinding> TodoBindings()
        => new Dictionary<string, DirectToolBinding>
        {
            [TodoPlanRules.ToolName] = new(AIFunctionFactory.Create(() => "ok", TodoPlanRules.ToolName),
                new DirectToolDescriptor(ToolCallKind.Other))
        };

    private static ConversationCompactionRecord CreateCompaction(long throughSequence)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationCompactionRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), throughSequence,
            Summary, "test-model", 1, 10, 5, now);
    }

    // The section only reads the plan, the run id rides along for callers that cache on it.
    private static ToolExecutionRecord CreateRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new ToolExecutionRecord(Guid.NewGuid(), Guid.NewGuid(), TodoPlanRules.ToolName, "{\"todos\":[]}",
            ToolExecutionStatus.Completed, null, null, null, now, now);
    }

    private static DirectTurnExecutionContext Interactive() => new(DirectTurnOrigin.Interactive, null, null);
}
