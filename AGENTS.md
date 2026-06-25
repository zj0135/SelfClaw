# SelfClaw - Project Context

## Overview

SelfClaw is a Windows desktop AI programming assistant built with WPF and .NET 10. The active workflow is a **direct programming agent** — the user types a prompt, the AI executes it with workspace tools, and the result renders in a WebView2-hosted Vue transcript.

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
  → MainWindowViewModel.SubmitPromptAsync()
  → SelfClawAgentChatRuntime.StreamTurnAsync()
    → ProduceProgrammingTurnAsync()
      → ChatClientAgentExecutionService.RunAsync()
        → AI model (OpenAI / Anthropic / OpenAI-compatible)
          → Tool calls → WorkspaceToolService (+ MCP if wired)
            → DesktopToolApprovalHandler (for write/shell)
  → Events back to ViewModel → TranscriptRenderState → Vue renders
```

Key runtime files (partial split under `Orchestration/`):
- `SelfClawAgentChatRuntime.cs` — DI wiring, stream entrypoint
- `SelfClawAgentChatRuntime.Execution.cs` — direct turn execution
- `SelfClawAgentChatRuntime.PromptMessages.cs` — prompt assembly
- `SelfClawAgentChatRuntime.Instructions.cs` — instruction builder

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

Write and shell tools require approval. Flow:
1. `ToolInvocationMetadata.RequiresApproval` checked
2. `DesktopToolApprovalHandler.RequestApprovalAsync()` emits toast + awaits `TaskCompletionSource<bool>`
3. `RuntimeToolObserver` tracks lifecycle: Start → AwaitingApproval → Running → Completed/Failed/Cancelled

### WPF shell

- `MainWindow.xaml` — custom chrome, title bar buttons, two-column layout (WebView2 + RightPanel stub)
- `LeftSidebar.xaml` — sidebar with Settings entry
- Settings view: `SelfClaw.TranscriptVue/src/components/Settings.vue` — **frontend only, mock data, no backend**
- RightPanel is a placeholder (width=0, collapsed by default)

### DI Registration

Infrastructure (`ServiceCollectionExtensions.AddSelfClawInfrastructure()`):
- Repositories: `SqliteProfileRepository`, `SqliteConversationRepository`, `SqliteAiProviderRepository`
- Providers: `OpenAiProviderAdapter`, `AnthropicProviderAdapter`, `AiProviderRegistry`
- Runtime: `SelfClawAgentChatRuntime`, `ChatClientAgentExecutionService`, `McpServerToolProvider`
- Tools: `WorkspaceToolService`, `FileSystemAgentContextProviderFactory`, `RuntimeToolObserver`
- Security: `DpapiSecretProtector`

Desktop (`App.xaml.cs`):
- `DesktopAgentStore`, `DesktopToolApprovalHandler`, `DesktopNotificationService`, `SystemTrayService`, `MainWindowViewModel`, `MainWindow`

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

Schema version: **17** (in `SqliteDatabase.cs`). Tables: `profiles`, `ai_provider_connections`, `ai_model_profiles`, `ai_model_profile_selections`, `workspace_roots`, `conversations`, `messages`, `message_attachments`, `tool_runs`. Backward-compatible column migration via `EnsureColumnExistsAsync`.

### Image attachments

Persisted to `{AppData}\attachments\{convId}\{msgId}\`. Max 6 images, 10MB each, 30MB total. Served to WebView2 via `https://attachments.selfclaw.local/{path}`.

### Transient state

`TranscriptRenderState` is the DTO published to the Vue frontend. The segmenter (`AssistantMessageSegmenter`) parses `<thinking>` blocks and tool anchors for rendering.

## Dead / Retained Code (NOT active)

- **Feishu channel**: fully implemented but never registered in DI
- **Plan mode**: removed, `AgentExecutionMode` has `Direct` only
- **Channel conversations**: data model retained but VM filters them out
- **MCP server wiring**: provider exists but VM passes empty list
- **Settings backend**: removed, Vue Settings page uses mock data
- **RightPanel**: XAML stub, not functional
