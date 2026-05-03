# SelfClaw - Project Context

## Overview

SelfClaw is a Windows-first personal AI assistant desktop app built with WPF and .NET 10.

Main projects:

- `SelfClaw.Core`: domain models, interfaces, runtime request/event contracts
- `SelfClaw.Infrastructure`: agent runtime, SQLite persistence, tools, security, channel integrations
- `SelfClaw.Desktop`: WPF desktop app, ViewModels, notifications, tray, channel management
- `SelfClaw.Tests`: xUnit test project, mostly mirrors Infrastructure layout
- `SelfClaw.TranscriptVue`: Vue + Vite transcript shell, build output goes to Desktop assets

## Core Capabilities

- Multi-provider profiles (OpenAI-compatible endpoint/model/sampling settings)
- Conversation modes: `Programming`, `Channel`
- `Programming` mode supports Plan Mode
- `Channel` mode supports external message ingestion (currently Feishu)
- Workspace tools support file read/write, full-text search, PowerShell execution with approval
- Transcript supports thinking segments and tool anchors
- Message image attachments persisted in `message_attachments`
- API keys are encrypted with Windows DPAPI

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
| Channel | Feishu Open Platform |

## Project Structure (Current)

```text
SelfClaw/
├── AGENTS.md
├── SelfClaw.slnx
├── SelfClaw.Core/
│   ├── Interfaces/
│   ├── Models/
│   └── Runtime/
├── SelfClaw.Infrastructure/
│   ├── Agents/
│   │   ├── Runtime/
│   │   └── Tools/
│   ├── Channels/
│   │   └── Feishu/
│   │       ├── Contracts/
│   │       ├── Enums/
│   │       ├── Helpers/
│   │       ├── Models/
│   │       └── Protocol/
│   ├── Data/
│   │   └── Sqlite/
│   │       └── Repositories/
│   ├── DependencyInjection/
│   ├── Options/
│   ├── Security/
│   └── Tools/
│       ├── Transcript/
│       └── Workspace/
├── SelfClaw.Desktop/
│   ├── Services/
│   │   ├── Channels/
│   │   ├── Settings/
│   │   ├── Tools/
│   │   └── Transcript/
│   ├── ViewModels/
│   └── Assets/
│       └── TranscriptVue/
├── SelfClaw.TranscriptVue/
│   ├── src/
│   ├── package.json
│   └── vite.config.js
└── SelfClaw.Tests/
    └── Infrastructure/
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

- `SelfClaw.Infrastructure.Agents.Runtime`
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
- `IAgentContextProviderFactory`
- `IAgentChatRuntime`
- `MarkdownHtmlRenderer`

Desktop registration in `App.xaml.cs` includes:

- `DesktopSettingsStore`
- `DesktopChannelManager`
- `DesktopToolApprovalHandler`
- `DesktopNotificationService`
- `SystemTrayService`
- `MainWindowViewModel`, `MainWindow`

### Agent runtime

`SelfClawAgentChatRuntime` orchestrates:

- `Programming`
- `Programming + EnablePlanMode`
- `Channel`

Supporting components:

- `ChatClientAgentExecutionService`
- `FileSystemAgentContextProviderFactory`
- `WorkspaceToolFunctions`
- `RuntimeToolObserver`

Runtime internal structure (partial split):

- `SelfClawAgentChatRuntime.cs`: constants, DI wiring, stream entrypoint, tool/context factory
- `SelfClawAgentChatRuntime.Execution.cs`: programming turn, plan drafting, and plan step execution
- `SelfClawAgentChatRuntime.PromptMessages.cs`: prompt-message assembly and message mapping
- `SelfClawAgentChatRuntime.Instructions.cs`: instruction builder methods
- `SelfClawAgentChatRuntime.Transcripts.cs`: plan transcript and plan-text sanitization helpers
- `SelfClawAgentChatRuntime.Parsing.cs`: JSON extraction/parsing, fallbacks, slug/title helpers
- `SelfClawAgentChatRuntime.Models.cs`: private runtime records and empty context-provider fallback

### Plan Mode

Core runtime includes:

- `ExecutionPlan`
- `ExecutionPlanStep`
- `ExecutionPlanStepStatus`
- `ExecutionPlanDraftingStartedEvent`
- `ExecutionPlanPreparedEvent`
- `ExecutionPlanStepStatusChangedEvent`

Desktop transcript models include:

- `TranscriptPlanPanel`
- `TranscriptPlanStep`

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

- `CurrentSchemaVersion = 14`
- `PRAGMA foreign_keys = ON`
- backward-compatible `EnsureColumnExists`

Main tables:

- `profiles`
- `workspace_roots`
- `conversations`
- `messages`
- `message_attachments`
- `tool_runs`

### Transcript and markdown

`SelfClaw.Infrastructure.Tools.Transcript` includes:

- `AssistantMessageSegmenter`
- `AssistantMessageSegmentKind`
- `AssistantMessageSegment`
- `AssistantMessageSegments`
- `MarkdownHtmlRenderer`

### Feishu channel structure

`SelfClaw.Infrastructure.Channels.Feishu` is split by responsibility:

- `Models/`: channel models and records
- `Enums/`: file/urgent/resource enums
- `Contracts/`: streaming reply contract
- `Helpers/`: value conversion helpers
- `Protocol/`: long-connection frame/header/protobuf/exception types

Desktop channel orchestration remains in `DesktopChannelManager` + `FeishuDesktopChannelAdapter`.

## Key File Index (Current)

| File | Purpose |
| --- | --- |
| `SelfClaw.Desktop/App.xaml.cs` | App entry, logging, DI setup |
| `SelfClaw.Desktop/ViewModels/MainWindowViewModel*.cs` | Main conversation/channel workflows |
| `SelfClaw.Desktop/Services/Settings/DesktopSettingsStore.cs` | Desktop settings load/save/normalize |
| `SelfClaw.Desktop/Services/Settings/DesktopSettings.cs` | Desktop settings model |
| `SelfClaw.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` | Infrastructure service registration |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.cs` | Runtime orchestration core |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.Execution.cs` | Programming/plan execution flow |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.PromptMessages.cs` | Prompt message assembly and role/message mapping |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.Instructions.cs` | Instruction builder methods |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.Transcripts.cs` | Discussion and execution transcript builders |
| `SelfClaw.Infrastructure/Agents/Runtime/SelfClawAgentChatRuntime.Parsing.cs` | Runtime JSON parsing and fallback builders |
| `SelfClaw.Infrastructure/Agents/Runtime/ChatClientAgentExecutionService.cs` | Model execution layer |
| `SelfClaw.Infrastructure/Agents/Tools/WorkspaceToolFunctions.cs` | Tool wrapping and approval integration |
| `SelfClaw.Infrastructure/Tools/Workspace/WorkspaceToolService.cs` | Workspace files and shell implementation |
| `SelfClaw.Infrastructure/Data/Sqlite/SqliteDatabase.cs` | SQLite schema and migration |
| `SelfClaw.Infrastructure/Data/Sqlite/Repositories/SqliteConversationRepository.cs` | Conversation/message/tool persistence |
| `SelfClaw.Infrastructure/Channels/Feishu/Protocol/FeishuWsFrame.cs` | Feishu long-connection frame model |
| `SelfClaw.Infrastructure/Channels/Feishu/Models/FeishuIncomingMessage.cs` | Feishu incoming message model |
| `SelfClaw.TranscriptVue/src/*` | Transcript frontend shell |

## Notes

1. The project is Windows-first (WPF, DPAPI, PowerShell, tray/notification integrations).
2. Workspace tools are not read-only; they can write files and execute shell commands.
3. Security boundaries depend on `WorkspaceToolService` path checks and `ToolPermissionMode`.
4. Core folder grouping and namespaces are intentionally not 1:1.
5. `SelfClaw.Desktop.csproj` already suppresses `NU1510` via `NoWarn`.
