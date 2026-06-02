# SelfClaw - Project Context

## Overview

SelfClaw is a Windows-first personal AI assistant desktop app built with WPF and .NET 10.

Main projects:

- `SelfClaw.Core`: domain models, interfaces, runtime request/event contracts
- `SelfClaw.Infrastructure`: agent runtime, SQLite persistence, workspace tools, security, Feishu implementation
- `SelfClaw.Desktop`: WPF desktop app, ViewModels, notifications, tray, retained channel adapters
- `SelfClaw.Tests`: xUnit test project, mostly mirrors Infrastructure layout
- `SelfClaw.TranscriptVue`: Vue + Vite transcript shell, build output goes to Desktop assets

## Current Product Shape

- The active desktop workflow is the main programming agent only.
- `AgentExecutionMode` currently has `Direct` only.
- Plan mode has been removed from active runtime, core events, desktop transcript models, and TranscriptVue.
- The old desktop system settings model/store has been removed. The current WPF settings panel still exists and should be treated as the WPF UI surface for future settings work.
- Feishu/channel implementation is retained, but channel manager/adapters are not registered by default in `App.xaml.cs`.
- `ConversationMode.Channel` remains for retained channel code and persistence compatibility; the desktop VM filters active sidebar/history behavior to programming conversations.

## Core Capabilities

- Multi-provider profiles (OpenAI-compatible endpoint/model/sampling settings)
- Direct programming conversations using the selected model profile and selected workspace
- Agent definitions loaded from markdown files under the app data `agents` directory
- Workspace tools support file read/write, full-text search, and PowerShell execution with approval
- Transcript supports thinking segments, tool anchors, tool activity, image attachments, and conversation navigation
- Message image attachments are persisted in `message_attachments`
- Conversation context summaries are persisted in `conversation_context_summaries`
- API keys are encrypted with Windows DPAPI
- Feishu Open Platform implementation is retained for later channel wiring

## Tech Stack

| Layer | Tech |
| --- | --- |
| Runtime | .NET 10 |
| Desktop UI | WPF, CommunityToolkit.Mvvm |
| AI | Microsoft.Agents.AI, Microsoft.Extensions.AI, Microsoft.Extensions.AI.OpenAI |
| Data | SQLite (`Microsoft.Data.Sqlite`) |
| Render | WebView2, Markdig, TranscriptVue (Vue 3 + Vite) |
| Logging | Serilog |
| Security | Windows DPAPI |
| Retained channel implementation | Feishu Open Platform |

## Project Structure (Current)

```text
SelfClaw/
|-- AGENTS.md
|-- SelfClaw.slnx
|-- SelfClaw.Core/
|   |-- Interfaces/
|   |-- Models/
|   `-- Runtime/
|-- SelfClaw.Infrastructure/
|   |-- Agents/
|   |   |-- Runtime/
|   |   |   |-- Compaction/
|   |   |   |-- Context/
|   |   |   |-- Execution/
|   |   |   |-- Mcp/
|   |   |   |-- Orchestration/
|   |   |   `-- Tools/
|   |   `-- Tools/
|   |-- Channels/
|   |   `-- Feishu/
|   |       |-- Contracts/
|   |       |-- Enums/
|   |       |-- Helpers/
|   |       |-- Models/
|   |       `-- Protocol/
|   |-- Data/
|   |   `-- Sqlite/
|   |       `-- Repositories/
|   |-- DependencyInjection/
|   |-- Options/
|   |-- Security/
|   `-- Tools/
|       |-- Transcript/
|       `-- Workspace/
|-- SelfClaw.Desktop/
|   |-- Controls/
|   |-- Services/
|   |   |-- Agents/
|   |   |-- Channels/
|   |   |-- Editors/
|   |   |-- Settings/
|   |   |-- Sidebar/
|   |   |-- Tools/
|   |   |-- Transcript/
|   |   `-- Windowing/
|   |-- ViewModels/
|   `-- Assets/
|       `-- TranscriptVue/
|-- SelfClaw.TranscriptVue/
|   |-- src/
|   |-- package.json
|   `-- vite.config.js
`-- SelfClaw.Tests/
    `-- Infrastructure/
```

## Important Conventions

### 1. Core namespaces are stable

Even though `SelfClaw.Core` is grouped by feature folders, namespaces stay:

- `SelfClaw.Core.Interfaces`
- `SelfClaw.Core.Models`
- `SelfClaw.Core.Runtime`

Prefer these stable namespaces for new types unless a deliberate namespace refactor is planned.

### 2. Infrastructure namespaces mostly follow folders

Common namespaces:

- `SelfClaw.Infrastructure.Agents.Runtime.Orchestration`
- `SelfClaw.Infrastructure.Agents.Runtime.Execution`
- `SelfClaw.Infrastructure.Agents.Runtime.Context`
- `SelfClaw.Infrastructure.Agents.Runtime.Mcp`
- `SelfClaw.Infrastructure.Agents.Runtime.Tools`
- `SelfClaw.Infrastructure.Agents.Runtime.Compaction`
- `SelfClaw.Infrastructure.Agents.Tools`
- `SelfClaw.Infrastructure.Data.Sqlite`
- `SelfClaw.Infrastructure.Data.Sqlite.Repositories`
- `SelfClaw.Infrastructure.Tools.Transcript`
- `SelfClaw.Infrastructure.Tools.Workspace`
- `SelfClaw.Infrastructure.Channels.Feishu`

### 3. Tests use ProjectReference

`SelfClaw.Tests.csproj` references:

- `..\SelfClaw.Core\SelfClaw.Core.csproj`
- `..\SelfClaw.Infrastructure\SelfClaw.Infrastructure.csproj`

### 4. TranscriptVue output is overwritten by frontend build

`SelfClaw.TranscriptVue/vite.config.js` outputs to:

- `SelfClaw.Desktop/Assets/TranscriptVue`

Desktop build then copies this folder via `SyncTranscriptVueAssets`.

## Build and Run

### Requirements

- Windows 10/11
- .NET SDK 10.0.201+
- WebView2 Runtime
- Node.js + npm (for TranscriptVue development)

### Commands

```powershell
dotnet restore SelfClaw.slnx --force-evaluate
dotnet build SelfClaw.slnx
dotnet run --project SelfClaw.Desktop/SelfClaw.Desktop.csproj
dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj
```

### TranscriptVue

```powershell
cd SelfClaw.TranscriptVue
npm install
npm run dev
npm run build
```

## Architecture Notes

### Dependency injection

Infrastructure registration in `ServiceCollectionExtensions.AddSelfClawInfrastructure()` includes:

- `StoragePaths`
- `SqliteDatabase`
- `IProfileRepository`, `IConversationRepository`
- `ISecretProtector`
- `IWorkspaceToolService`
- `IAgentExecutionService`
- `IAgentContextProviderFactory`
- `IWorkspaceMemoryInitializationService`
- `IAgentMcpToolProvider`
- `IAgentChatRuntime`
- `IConversationContextCompactionService`
- `MarkdownHtmlRenderer`

Desktop registration in `App.xaml.cs` currently includes:

- `DesktopAgentStore`
- `DesktopToolApprovalHandler`
- `DesktopNotificationService`
- `DesktopNotificationActivationService`
- `SystemTrayService`
- `MainWindowViewModel`, `MainWindow`

Desktop no longer registers `DesktopSettingsStore`, `DesktopChannelManager`, Feishu adapters, or slash command handlers.

### Agent runtime

`SelfClawAgentChatRuntime` now always produces a direct programming turn through `ProduceProgrammingTurnAsync`.

Supporting components:

- `ChatClientAgentExecutionService`
- `FileSystemAgentContextProviderFactory`
- `McpServerToolProvider`
- `ConversationContextCompactionService`
- `WorkspaceToolFunctions`
- `RuntimeToolObserver`

Runtime folder structure:

- `Orchestration/`: `SelfClawAgentChatRuntime` stream orchestration, direct turn execution, prompt assembly, instructions, and private models
- `Execution/`: model client execution contracts, request/response records, and OpenAI-compatible chat execution
- `Context/`: file-system backed agent skill/context provider discovery
- `Mcp/`: MCP server connection, tool discovery, tool naming, and owned resource lifetime
- `Tools/`: runtime tool-run observation and approval/result metadata
- `Compaction/`: conversation context auto-compaction, summary prompt building, and token/window estimates

`Orchestration/SelfClawAgentChatRuntime` partial split:

- `SelfClawAgentChatRuntime.cs`: constants, DI wiring, stream entrypoint, tool/context factory
- `SelfClawAgentChatRuntime.Execution.cs`: direct programming turn execution
- `SelfClawAgentChatRuntime.PromptMessages.cs`: prompt message assembly and role/message mapping
- `SelfClawAgentChatRuntime.Instructions.cs`: programming instruction builder and capability hints
- `SelfClawAgentChatRuntime.Models.cs`: private runtime records and empty provider fallbacks

### Desktop ViewModel

`MainWindowViewModel` owns the active desktop programming workflow:

- profile, workspace, agent, conversation, message, tool-run, and runtime state
- WebView transcript state publishing through `TranscriptRenderState`
- selected agent resolution from `DesktopAgentStore`
- image attachment persistence under app data
- tool approval notifications
- context compaction before and after successful turns

`MainWindowViewModel.Agents.cs` resolves runtime agents as `AgentExecutionMode.Direct`. Agent skills from agent markdown are passed through; configured MCP servers are currently empty until a new settings surface is implemented.

`MainWindowViewModel.Notifications.cs` handles Windows toast activation for programming conversations only.

`MainWindowViewModel.RuntimeState.cs` tracks running conversations, active messages, status text, and cancellation.

### Agent definitions

`DesktopAgentStore` stores agent markdown files under the app data `agents` directory.

The built-in agent id is:

- `build`

Agent markdown still supports fields for skills and MCP server ids, but MCP configuration is not currently wired from desktop settings. Missing skills produce warnings; MCP ids are persisted in agent files but are not converted to configured runtime MCP servers by the desktop VM yet.

### WPF shell and settings

The desktop app uses a WPF shell around a WebView2 transcript host:

- `MainWindow.xaml` owns the window chrome, toolbar, transcript WebView, right panel, terminal drawer, and WPF settings overlay host.
- `LeftSidebar.xaml` owns the current sidebar including the Settings entry.
- `SystemSettingsPanel.xaml` is the current WPF settings panel surface and should remain in place while the new settings implementation is designed.

The old non-WPF `DesktopSettings` / `DesktopSettingsStore` model was removed. `SystemThemeReader` remains for following the Windows app theme.

### Retained channel and Feishu implementation

Feishu implementation remains under:

- `SelfClaw.Infrastructure/Channels/Feishu`
- `SelfClaw.Desktop/Services/Channels/FeishuDesktopChannelAdapter.cs`

Desktop channel orchestration code also remains:

- `DesktopChannelManager`
- `DesktopChannelSettingsStore`
- `DesktopChannelSettings`
- `DesktopChannelConfiguration`
- `IDesktopChannelAdapter`, `IDesktopChannelConnection`

These are retained implementation pieces and are not active in default desktop DI. `DesktopChannelSettingsStore` uses `desktop-channel-settings.json` if channel orchestration is wired again later.

### Workspace tools and permissions

Tools:

- `list_workspace_files`
- `search_workspace_text`
- `read_workspace_file`
- `write_workspace_file`
- `run_shell_command`

Permission model:

- `RequireApproval`
- `FullAccess`

`WorkspaceToolService` handles path normalization/sandboxing, text checks, size limits, and shell timeout.

### Data and persistence

`SqliteDatabase` manages schema initialization/migration:

- `CurrentSchemaVersion = 15`
- `PRAGMA foreign_keys = ON`
- backward-compatible `EnsureColumnExists`

Main tables:

- `profiles`
- `workspace_roots`
- `conversations`
- `messages`
- `message_attachments`
- `conversation_context_summaries`
- `tool_runs`

### Transcript and markdown

`SelfClaw.Infrastructure.Tools.Transcript` includes:

- `AssistantMessageSegmenter`
- `AssistantMessageSegmentKind`
- `AssistantMessageSegment`
- `AssistantMessageSegments`
- `MarkdownHtmlRenderer`

Desktop transcript service models include:

- `TranscriptRenderState`
- `TranscriptRenderItem`
- `TranscriptRenderSegment`
- `TranscriptConversationItem`
- `TranscriptImageAttachment`
- `ToolRunAnchor`
- `ToolRunPlacement`
- `TranscriptToolRunPresenter`
- `AgentActivityNode`

## Key File Index (Current)

| File | Purpose |
| --- | --- |
| `SelfClaw.Desktop/App.xaml.cs` | App entry, logging, DI setup |
| `SelfClaw.Desktop/MainWindow.xaml` | WPF shell layout around TranscriptVue WebView2 |
| `SelfClaw.Desktop/Controls/SystemSettingsPanel.xaml` | Current WPF settings panel surface |
| `SelfClaw.Desktop/ViewModels/MainWindowViewModel*.cs` | Main programming conversation workflow |
| `SelfClaw.Desktop/Services/Agents/DesktopAgentStore.cs` | Agent markdown load/save and built-in agent provisioning |
| `SelfClaw.Desktop/Services/Channels/DesktopChannelSettingsStore.cs` | Retained channel configuration persistence |
| `SelfClaw.Desktop/Services/Channels/DesktopChannelManager.cs` | Retained desktop channel orchestration |
| `SelfClaw.Desktop/Services/Channels/FeishuDesktopChannelAdapter.cs` | Retained Feishu desktop adapter |
| `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` | Infrastructure service registration |
| `SelfClaw.Infrastructure/Agents/Runtime/Orchestration/SelfClawAgentChatRuntime.cs` | Runtime orchestration core |
| `SelfClaw.Infrastructure/Agents/Runtime/Orchestration/SelfClawAgentChatRuntime.Execution.cs` | Direct programming turn execution |
| `SelfClaw.Infrastructure/Agents/Runtime/Orchestration/SelfClawAgentChatRuntime.PromptMessages.cs` | Prompt message assembly and role/message mapping |
| `SelfClaw.Infrastructure/Agents/Runtime/Orchestration/SelfClawAgentChatRuntime.Instructions.cs` | Instruction builder methods |
| `SelfClaw.Infrastructure/Agents/Runtime/Execution/ChatClientAgentExecutionService.cs` | Model execution layer |
| `SelfClaw.Infrastructure/Agents/Runtime/Context/FileSystemAgentContextProviderFactory.cs` | Agent skill/context provider discovery |
| `SelfClaw.Infrastructure/Agents/Runtime/Mcp/McpServerToolProvider.cs` | MCP server tool discovery and wrapping |
| `SelfClaw.Infrastructure/Agents/Runtime/Tools/RuntimeToolObserver.cs` | Tool-run event emission for transcripts |
| `SelfClaw.Infrastructure/Agents/Runtime/Tools/ToolInvocationMetadata.cs` | Tool approval/result metadata and summaries |
| `SelfClaw.Infrastructure/Agents/Runtime/Compaction/ConversationContextCompactionService.cs` | Conversation context auto-compaction flow |
| `SelfClaw.Infrastructure/Agents/Runtime/Compaction/ConversationCompactionPromptBuilder.cs` | Compaction summary prompt/payload construction |
| `SelfClaw.Infrastructure/Agents/Runtime/Compaction/ConversationContextTokens.cs` | Token/window estimate helpers for compaction |
| `SelfClaw.Infrastructure/Agents/Tools/WorkspaceToolFunctions.cs` | Tool wrapping and approval integration |
| `SelfClaw.Infrastructure/Tools/Workspace/WorkspaceToolService.cs` | Workspace files and shell implementation |
| `SelfClaw.Infrastructure/Data/Sqlite/SqliteDatabase.cs` | SQLite schema and migration |
| `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteConversationRepository.cs` | Conversation/message/tool persistence |
| `SelfClaw.Infrastructure/Channels/Feishu/FeishuBotService.cs` | Feishu high-level bot service |
| `SelfClaw.Infrastructure/Channels/Feishu/Protocol/FeishuWsFrame.cs` | Feishu long-connection frame model |
| `SelfClaw.Infrastructure/Channels/Feishu/Models/FeishuIncomingMessage.cs` | Feishu incoming message model |
| `SelfClaw.TranscriptVue/src/*` | Transcript frontend shell |

## Notes

1. The project is Windows-first (WPF, DPAPI, PowerShell, tray/notification integrations).
2. Workspace tools are not read-only; they can write files and execute shell commands.
3. Security boundaries depend on `WorkspaceToolService` path checks and `ToolPermissionMode`.
4. Core folder grouping and namespaces are intentionally not 1:1.
5. Plan mode files and old desktop settings files are intentionally removed.
6. Feishu/channel code is retained but currently not active in default desktop startup.
7. `SelfClaw.Desktop.csproj` already suppresses `NU1510` via `NoWarn`.
