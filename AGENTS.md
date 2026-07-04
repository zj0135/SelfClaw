# SelfClaw - Project Context

## Overview

SelfClaw is a Windows desktop AI programming assistant built with WPF and .NET 10. The active workflow drives a **local coding agent CLI** (Claude Code / Codex / OpenCode) — the user types a prompt, the selected CLI executes it in the workspace as a subprocess, and its event stream renders in a WebView2-hosted Vue transcript.

## Projects

| Project | Role |
|---------|------|
| `SelfClaw.Core` | Domain models, interfaces, runtime contracts (pure, no external deps) |
| `SelfClaw.Infrastructure` | Agent runtime, SQLite repos, workspace tools, security, AI provider adapters |
| `SelfClaw.Desktop` | WPF shell, ViewModels, notifications, tray, WebView2 host |
| `SelfClaw.Tests` | xUnit tests (mirrors Infrastructure layout) |
| `SelfClaw.TranscriptVue` | Vue 3 + Vite frontend shell, builds to `Desktop/Assets/TranscriptVue` |

## Build & Run

```powershell
dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
```

TranscriptVue dev: `cd SelfClaw.TranscriptVue && npm install && npm run dev`

## Architecture

### Active workflow (only one)

```
User input (WebView2)
  → MainWindowViewModel.SubmitPromptAsync() → SendAsync()
    → resolves the selected local CLI (ProgrammingAssistantSettingsService.GetSelectedCliKindAsync)
    → DispatchingAgentChatRuntime.StreamTurnAsync() (Mode=Cli → CliAgentChatRuntime)
      → CliSessionResolver (resume ids) + CliAgentRegistry (per-CLI definition)
        → CliCommandResolver (PATH/PATHEXT, cmd.exe wrapping) → CliAgentProcessHost (subprocess)
          → stdout JSONL → ClaudeStreamJsonParser / JsonEventStreamParser → AgentStreamEvents
  → MainWindowViewModel.HandleAgentStreamEventAsync → TranscriptRenderState → Vue renders
```

The turn runs a local coding agent CLI (Claude Code / Codex / OpenCode) as a subprocess; the CLI uses
its own local auth/model config. Which CLI runs is the user's selection persisted by
`ProgrammingAssistantSettingsService` (settings page 编程助手 and the composer's ModelSelector share it
via the `get-programming-assistant-settings` / `select-programming-cli` WebView messages), carried on
`ChatTurnRequest.CliAgent`. No selection (no CLI detected) fails the turn with guidance.

Key runtime files (`Infrastructure/Agents/Cli/`):
- `CliAgentChatRuntime.cs` — one turn: session plan → args → spawn → parse → events
- `Definitions/` — `ClaudeAgentDefinition`, `CodexAgentDefinition`, `OpenCodeAgentDefinition`, `CliAgentRegistry`
- `Parsers/` — `ClaudeStreamJsonParser` (stream-json), `JsonEventStreamParser` (Codex/OpenCode)
- `Process/` — `CliCommandResolver`, `CliAgentProcessHost`, `CliAgentProcessSession` (watchdog, kill-tree)
- `Session/` — `CliSessionResolver`, `SqliteCliAgentSessionStore` (resume id per conversation × CLI)
- `Agents/Runtime/DispatchingAgentChatRuntime.cs` — mode dispatch (Direct reserved, fails cleanly)

### Desktop ViewModel

`MainWindowViewModel` (split into partial files) owns the programming workflow:
- `MainWindowViewModel.cs` — entry, submission, image attachments, theme following
- `MainWindowViewModel.Agents.cs` — resolves `DesktopAgentDefinition` → `AgentRuntimeDefinition(mode=Direct)`
- `MainWindowViewModel.Transcript.cs` — delta streaming, markdown merge, tool anchors
- `MainWindowViewModel.Notifications.cs` — toast notifications
- `MainWindowViewModel.RuntimeState.cs` — running conversation tracking

### Agent definitions

`DesktopAgentStore` loads `.md` files from `{AppData}\agents\`. Built-in agent id: `build`.
Agent markdown supports front matter: name, description, mode, tools, skills, mcpServers.
**Important**: MCP servers are defined in markdown but `MainWindowViewModel.Agents.cs` passes an empty list — configured MCP servers are always empty at runtime.

### Tool approval

Not active in the CLI workflow: the CLI agent applies its own permission policy, and the desktop
approval UI was removed with the frontend rework. `DesktopToolApprovalHandler` is still registered and
passed on `ChatTurnRequest`, but nothing subscribes to it.

### WPF shell

- `MainWindow.xaml` — custom chrome, title bar buttons, two-column layout (WebView2 + RightPanel stub)
- `LeftSidebar.xaml` — sidebar with Settings entry
- Settings view: `SelfClaw.TranscriptVue/src/components/Settings.vue` — **frontend only, mock data, no backend**
- RightPanel is a placeholder (width=0, collapsed by default)

### DI Registration

Infrastructure (`ServiceCollectionExtensions.AddSelfClawInfrastructure()`):
- Repositories: `SqliteProfileRepository`, `SqliteConversationRepository`, `SqliteAiProviderRepository`
- CLI runtime: `CliCommandResolver`, `CliAgentProcessHost`, `CliAgentRegistry`, `SqliteCliAgentSessionStore`,
  `CliSessionResolver`, `CliAgentChatRuntime`, `DispatchingAgentChatRuntime` (as `IAgentChatRuntime`)
- Tools: `WorkspaceToolService`, `MarkdownHtmlRenderer`
- Security: `DpapiSecretProtector`

Desktop (`App.xaml.cs`):
- `DesktopAgentStore`, `DesktopSettingsJsonStore`, `DesktopToolApprovalHandler`, `DesktopNotificationService`,
  `DesktopNotificationActivationService`, `ProgrammingAssistantSettingsService`, `PetService`,
  `SystemTrayService`, `MainWindowViewModel`, `MainWindow`

**Not registered** (retained/dead): `DesktopChannelManager`, Feishu adapters, old `DesktopSettingsStore`.

## Key Conventions

### Stable namespaces (Core)
- `SelfClaw.Core.Interfaces`
- `SelfClaw.Core.Models`
- `SelfClaw.Core.Runtime`

### Common namespaces (Infrastructure)
- `SelfClaw.Infrastructure.Agents.Runtime.{Orchestration,Execution,Context,Mcp,Tools}`
- `SelfClaw.Infrastructure.Data.Sqlite.{Repositories}`
- `SelfClaw.Infrastructure.Tools.{Transcript,Workspace}`
- `SelfClaw.Infrastructure.AiProviders.{OpenAi,Anthropic}`

### Database

Schema version: **19** (in `SqliteDatabase.cs`). Tables: `profiles`, `ai_provider_connections`, `ai_model_profiles`, `ai_model_profile_selections`, `workspace_roots`, `conversations`, `messages`, `message_attachments`, `tool_runs`, `cli_agent_sessions`. Backward-compatible column migration via `EnsureColumnExistsAsync`.

### Image attachments

Persisted to `{AppData}\attachments\{convId}\{msgId}\`. Max 6 images, 10MB each, 30MB total. Served to WebView2 via `https://attachments.selfclaw.local/{path}`.

### Transient state

`TranscriptRenderState` is the DTO published to the Vue frontend. The segmenter (`AssistantMessageSegmenter`) parses `<thinking>` blocks and tool anchors for rendering.

## Dead / Retained Code (NOT active)

- **Feishu channel**: fully implemented but never registered in DI
- **Plan mode**: removed, `AgentExecutionMode` has `Direct` (reserved, no runtime) and `Cli` (active)
- **Channel conversations**: data model retained but VM filters them out
- **MCP server wiring**: provider exists but VM passes empty list
- **Settings pages**: 编程助手 (CLI scan/select) and 宠物 are wired to the host; the other settings pages are frontend mock
- **Profile / API key on ChatTurnRequest**: resolved and passed but unused by the CLI runtime (reserved for a future Direct runtime)
- **RightPanel**: XAML stub, not functional
