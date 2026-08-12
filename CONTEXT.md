# SelfClaw

SelfClaw coordinates conversations, agents, and the filesystem context in which programming work runs.

## Language

**Workspace Root**:
A physical directory used as the working directory for a conversation, its tools, and its terminal.
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
