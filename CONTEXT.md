# SelfClaw

SelfClaw coordinates conversations, agents, and the filesystem context in which programming work runs.

## Language

**Workspace Root**:
A physical directory used as the working directory for a conversation, its tools, and its terminal.
Desktop selection is owned by the UI; workspace workflows prepare and release Managed Worktrees. A submitted turn carries its captured Workspace Root independently of later navigation.
_Avoid_: Project, repository

**Git Repository**:
The shared Git object and reference store identified by its common Git directory. One repository may have several checkouts.
_Avoid_: Workspace, project folder

**Git Checkout**:
A physical working tree belonging to a Git Repository and represented in SelfClaw by a Workspace Root.
_Avoid_: Branch

**Managed Worktree**:
A Git Checkout created and lifecycle-managed by SelfClaw for one interactive conversation.
_Avoid_: Temporary clone, session branch

**Base Branch**:
The local branch and committed HEAD from which a Managed Worktree was created and into which its Task Branch is merged.
_Avoid_: Main branch, parent branch

**Task Branch**:
The branch checked out by a Managed Worktree for the work owned by its conversation.
_Avoid_: Worktree

**Subagent Task**:
One durable, parent-owned Direct delegation with its own child conversation, isolated input, frozen execution settings, and lifecycle. Repeated invocations or retries are separate tasks, even when they use the same subagent definition.
_Avoid_: Agent definition, parent conversation message

**Subagent Delivery**:
The durable handoff of a completed Subagent Task result to its parent agent. Pending or failed delivery is separate from the child's execution status; it does not make a completed task run again.
_Avoid_: Child execution, transcript block

**Continuation Execution Checkpoint**:
A durable record committed under the current Subagent Delivery lease before a tool may execute. It means execution may have begun and prevents automatic replay after interruption; it is independent of transcript publication and does not promise exactly-once external effects.
_Avoid_: Tool-start display event, completed tool result

**Activity Panel**:
An independent view of the current parent's tasks and their live or persisted content. Its subscriptions, selected detail, reading position, and acknowledgement flow are separate from the main transcript and composer.
_Avoid_: Child conversation navigation, parent transcript
