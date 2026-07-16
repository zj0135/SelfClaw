# SelfClaw - Project Context

## Overview

SelfClaw is a Windows desktop AI programming assistant built with WPF and .NET 10. It supports two active execution modes selected by the current desktop agent: **Direct**, which calls configured AI providers in-process through Microsoft.Extensions.AI, and **CLI**, which runs Claude Code / Codex / OpenCode as a subprocess. Both modes emit the same event stream into a WebView2-hosted Vue transcript.

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

### Active workflows

```
User input (WebView2)
  → MainWindowViewModel.SubmitPromptAsync() → SendAsync()
    → resolves DesktopAgentDefinition.mode and builds ChatTurnRequest
    → DispatchingAgentChatRuntime.StreamTurnAsync()
      ├─ Mode=Direct → DirectAgentChatRuntime
      │   → AiChatClientFactory (selected/default model profile + protected credential)
      │   → provider IChatClient + WorkspaceAgentToolset + desktop approval
      │   → M.E.AI updates → AgentStreamEvents
      └─ Mode=Cli → CliAgentChatRuntime
          → CliSessionResolver + CliAgentRegistry
          → CliCommandResolver → CliAgentProcessHost (subprocess)
          → stdout JSONL → ClaudeStreamJsonParser / JsonEventStreamParser → AgentStreamEvents
  → MainWindowViewModel.HandleAgentStreamEventAsync → TranscriptRenderState → Vue renders
```

Direct mode uses the enabled model selected in the composer, or the `desktop.default` model profile when no explicit id is carried by `ChatTurnRequest.ModelProfileId`. Provider credentials are decrypted only inside Infrastructure. CLI mode uses the local CLI selection persisted by `ProgrammingAssistantSettingsService`; the CLI continues to own its local authentication and model configuration. No detected CLI selection fails a CLI turn with guidance.

Key runtime files:
- `Agents/Runtime/DispatchingAgentChatRuntime.cs` — dispatches Direct and CLI modes
- `Agents/Runtime/DirectAgentChatRuntime.cs` — in-process provider turn, event translation, usage and terminal-state discipline
- `Agents/Runtime/WorkspaceAgentToolset.cs` — workspace tools and approval wrapping for Direct turns
- `AiProviders/AiChatClientFactory.cs` — model/connection validation, credential resolution, adapter construction
- `AiProviders/AiProviderSettingsService.cs` — provider/model CRUD, discovery, enablement and default selection
- `CliAgentChatRuntime.cs` — one turn: session plan → args → spawn → parse → events
- `Definitions/` — `ClaudeAgentDefinition`, `CodexAgentDefinition`, `OpenCodeAgentDefinition`, `CliAgentRegistry`
- `Parsers/` — `ClaudeStreamJsonParser` (stream-json), `JsonEventStreamParser` (Codex/OpenCode)
- `Process/` — `CliCommandResolver`, `CliAgentProcessHost`, `CliAgentProcessSession` (watchdog, kill-tree)
- `Session/` — `CliSessionResolver`, `SqliteCliAgentSessionStore` (resume id per conversation × CLI)

### Desktop ViewModel

`MainWindowViewModel` (split into partial files) owns the programming workflow:
- `MainWindowViewModel.cs` — entry, submission, image attachments, theme following
- `MainWindowViewModel.Agents.cs` — preserves each `DesktopAgentDefinition`'s Direct/CLI mode in `AgentRuntimeDefinition`
- `MainWindowViewModel.Transcript.cs` — delta streaming, markdown merge, tool anchors
- `MainWindowViewModel.Notifications.cs` — toast notifications
- `MainWindowViewModel.RuntimeState.cs` — running conversation tracking

### Agent definitions

`DesktopAgentStore` loads `.md` files from `{AppData}\agents\`. Built-in agent id: `build`.
Agent markdown supports front matter: name, description, mode, tools, skills, mcpServers.
**Important**: MCP servers are defined in markdown but `MainWindowViewModel.Agents.cs` passes an empty list — configured MCP servers are always empty at runtime.

### Tool approval

Direct `write_file` and `run_shell_command` calls use `DesktopToolApprovalHandler` when the conversation is in `RequireApproval` mode. A visible window shows a WPF Yes/No prompt; a hidden/minimized window sends a Windows toast with Confirm/Cancel actions. Pending approvals default to rejection on timeout, subscriber failure, or window close. CLI mode continues to use the CLI's own permission policy.

### WPF shell

- `MainWindow.xaml` — custom chrome, title bar buttons, two-column layout (WebView2 + RightPanel stub)
- `LeftSidebar.xaml` — sidebar with Settings entry
- Settings view: AI 提供商, 编程助手, and 宠物 are connected to the desktop host; remaining pages are frontend placeholders/mock
- RightPanel is a placeholder (width=0, collapsed by default)

### DI Registration

Infrastructure (`ServiceCollectionExtensions.AddSelfClawInfrastructure()`):
- Repositories: `SqliteConversationRepository`, `SqliteAiProviderRepository`
- AI providers: catalog/registry, provider adapters, `AiProviderHttpClientProvider`, `AiProviderSettingsService`, `AiChatClientFactory`
- Runtimes: CLI process/session services, `CliAgentChatRuntime`, `DirectAgentChatRuntime`, `DispatchingAgentChatRuntime` (as `IAgentChatRuntime`)
- Tools: `WorkspaceToolService`, `WorkspaceAgentToolset`, `MarkdownHtmlRenderer`
- Security: `DpapiSecretProtector`

Desktop (`App.xaml.cs`):
- `DesktopAgentStore`, `DesktopSettingsJsonStore`, `DesktopToolApprovalHandler`, `DesktopNotificationService`,
  `DesktopNotificationActivationService`, `ProgrammingAssistantSettingsService`, `AiProviderSettingsBridge`, `PetService`,
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

Schema version: **21** (in `SqliteDatabase.cs`). Tables: `ai_provider_connections`, `ai_model_profiles`, `ai_model_profile_selections`, `workspace_roots`, `conversations`, `messages`, `message_attachments`, `tool_runs`, `cli_agent_sessions`. The obsolete `profiles` table and `conversations.profile_id` column are removed by the v21 migration while conversation-dependent rows are preserved. Backward-compatible additive migration still uses `EnsureColumnExistsAsync` where appropriate.

### Image attachments

Persisted to `{AppData}\attachments\{convId}\{msgId}\`. Max 6 images, 10MB each, 30MB total. Served to WebView2 via `https://attachments.selfclaw.local/{path}`.

### Transient state

`TranscriptRenderState` is the DTO published to the Vue frontend. The segmenter (`AssistantMessageSegmenter`) parses `<thinking>` blocks and tool anchors for rendering.

## Dead / Retained Code (NOT active)

- **Feishu channel**: fully implemented but never registered in DI
- **Plan mode**: removed; `AgentExecutionMode.Direct` and `AgentExecutionMode.Cli` are both active
- **Channel conversations**: data model retained but VM filters them out
- **MCP server wiring**: provider exists but VM passes empty list
- **Settings pages**: AI 提供商, 编程助手, and 宠物 are wired to the host; the other settings pages are frontend mock
- **Legacy provider profiles**: `ProviderProfile`, `IProfileRepository`, the `profiles` table, and `ChatTurnRequest.Profile/ApiKey` were removed; Direct turns use `ModelProfileId`
- **RightPanel**: XAML stub, not functional
