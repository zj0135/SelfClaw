# SelfClaw - Project Context

## Overview

SelfClaw is a Windows desktop AI programming assistant built with WPF and .NET 10. It supports two active execution modes selected by the current desktop agent: **Direct**, which calls configured AI providers in-process through Microsoft.Extensions.AI, and **CLI**, which runs Claude Code / Codex / OpenCode as a subprocess. Both modes emit the same event stream into a WebView2-hosted Vue transcript.

## Agent skills

### Issue tracker

Issues and specs live as markdown files under `.scratch/<feature-slug>/`. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context domain docs use root `CONTEXT.md` and `docs/adr/`. See `docs/agents/domain.md`.

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

`DesktopAgentDefinitionService` loads and atomically updates `.md` files from `{AppData}\agents\`. Built-in agent id: `build`.
Agent markdown supports front matter: name, description, mode, tools, plugins, skills, mcpServers. Direct turns resolve those ids against the enabled extension catalog; CLI turns keep their existing subprocess behavior.

### Tool approval

Direct `write_file` and `run_shell_command` calls use `DesktopToolApprovalHandler` when the conversation is in `RequireApproval` mode. A visible window shows a WPF Yes/No prompt; a hidden/minimized window sends a Windows toast with Confirm/Cancel actions. Pending approvals default to rejection on timeout, subscriber failure, or window close. CLI mode continues to use the CLI's own permission policy.

### WPF shell

- `MainWindow.xaml` — custom chrome, title bar buttons, two-column layout (WebView2 + RightPanel stub)
- `LeftSidebar.xaml` — sidebar with Settings entry
- Settings view: AI 提供商, 编程助手, and 宠物 are connected to the desktop host; remaining pages are frontend placeholders/mock
- RightPanel is a placeholder (width=0, collapsed by default)

### DI Registration

Infrastructure (`ServiceCollectionExtensions.AddSelfClawInfrastructure()`):
- Repositories: `SqliteConversationRepository`, `SqliteAiProviderRepository`, `SqliteExtensionRepository`
- AI providers: catalog/registry, provider adapters, `AiProviderHttpClientProvider`, `AiProviderSettingsService`, `AiChatClientFactory`
- Runtimes: CLI process/session services, `CliAgentChatRuntime`, `DirectAgentChatRuntime`, `DispatchingAgentChatRuntime` (as `IAgentChatRuntime`)
- Extensions: `ExtensionCatalog`, `ExtensionPackageInstaller`, `ExtensionSettingsService`, `ExtensionStateChangeNotifier`, `DirectTurnCapabilityResolver` plus its `SkillCapabilitySource` / `PluginCapabilitySource` / `McpCapabilitySource`, Skill readers/runtime tools
- MCP: configuration/transport factories, pooled `McpClientManager`, SDK connection factory, `McpToolAdapter`
- Tools: `WorkspaceToolService`, `WorkspaceAgentToolset`, `MarkdownHtmlRenderer`
- Security: `DpapiSecretProtector`

Desktop (`App.xaml.cs`):
- `DesktopAgentDefinitionService`, `ExtensionSettingsBridge`, `DesktopSettingsJsonStore`, `DesktopToolApprovalHandler`, `DesktopNotificationService`,
  `DesktopNotificationActivationService`, `ProgrammingAssistantSettingsService`, `AiProviderSettingsBridge`,
  `PetPackageCatalog`, `PetActivityPresenter`, `PetHost`, `SystemTrayService`, `MainWindowViewModel`, `MainWindow`

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

Schema version: **22** (in `SqliteDatabase.cs`). Tables: `ai_provider_connections`, `ai_model_profiles`, `ai_model_profile_selections`, `extension_packages`, `mcp_server_configs`, `workspace_roots`, `conversations`, `messages`, `message_attachments`, `tool_runs`, `cli_agent_sessions`. The v22 migration adds extension state and tool provenance while preserving existing conversations/messages/tool runs. Backward-compatible additive migration still uses `EnsureColumnExistsAsync` where appropriate.

### Image attachments

Persisted to `{AppData}\attachments\{convId}\{msgId}\`. Max 6 images, 10MB each, 30MB total. Served to WebView2 via `https://attachments.selfclaw.local/{path}`.

### Transient state

`TranscriptRenderState` is the DTO published to the Vue frontend. The segmenter (`AssistantMessageSegmenter`) parses `<thinking>` blocks and tool anchors for rendering.

## Dead / Retained Code (NOT active)

- **Feishu channel**: fully implemented but never registered in DI
- **Plan mode**: removed; `AgentExecutionMode.Direct` and `AgentExecutionMode.Cli` are both active
- **Channel conversations**: data model retained but VM filters them out
- **Settings pages**: AI 提供商, 编程助手, 扩展, and 宠物 are wired to the host; the remaining settings pages are frontend mock
- **Legacy provider profiles**: `ProviderProfile`, `IProfileRepository`, the `profiles` table, and `ChatTurnRequest.Profile/ApiKey` were removed; Direct turns use `ModelProfileId`
- **RightPanel**: XAML stub, not functional

## Code Style & Constraints

### DTOs vs. Business Logic

- **DTOs / models** are pure data carriers only — no methods, no business logic, no side effects. Use `record` types with primary constructors in `Core.Models` or `Infrastructure.AiProviders.Models`. Infrastructure-level view DTOs go in `Views/` subdirectories.
- **Methods / business logic** live exclusively in service classes, not in DTOs. A DTO should never call a service, access a database, or perform validation beyond constructor-level input contracts.
- Do **NOT** mix DTOs and service methods in the same file. Each file contains either one DTO or one service class — never both.

### Interface & Abstraction Placement

- **Domain contracts**（consumed by Core or by multiple projects） go in `SelfClaw.Core/Interfaces/<Feature>/`.
- **Infrastructure-internal abstractions**（only consumed within Infrastructure） go in `SelfClaw.Infrastructure/<Feature>/Abstractions/`.
- Prefer small, focused interfaces（1-5 methods）. Avoid fat interfaces that force consumers to implement unrelated concerns.

### Class Design

- `sealed class` on every class not deliberately designed for inheritance.
- Dependencies are constructor-injected as private `_camelCase` fields, listed before the constructor.
- Class layout order: fields → constructor → primary public method(s) → private helper methods.
- `internal sealed class` for DI-registered infrastructure types that are not `public` API.
- Extract private helper methods（`private static` where stateless） to keep public methods readable. A public method over 40-50 lines is a signal to decompose.

### Naming

- Async methods: always suffix `Async`.
- Static factory/exception helpers: `PascalCase` — e.g. `CreateForScopeAsync()`, `MissingApiKey()`, `MaskApiKey()`.
- `private static`: for stateless helper methods. `const` for compile-time constants.
- No abbreviations in public names — prefer `Conversation` over `Conv`, `Workspace` over `Ws`.

### Async & Streaming

- All library code（Infrastructure）uses `ConfigureAwait(false)`. Desktop ViewModels omit it.
- `OperationCanceledException` must always be re-thrown — never caught and converted into a failure result.
- Streaming uses `IAsyncEnumerable<T>` + `Channel<T>` pattern.

### Nullability & Safety

- Nullable reference types enabled project-wide. Use `string?`, `Guid?` etc. explicitly.
- Public methods guard with `ArgumentNullException.ThrowIfNull(...)`, not manual null checks.
- No nullable suppression (`!`) unless immediately after a provable null check the compiler cannot track.

### Code Simplicity

- Favor plain flow control (`if`/`return`/`switch`) over excessive abstraction layers. Do NOT introduce generic wrappers or base classes for a single caller.
- Use `switch` expressions for dispatch-based branching.
- Avoid over-encapsulation: a three-line helper that is called exactly once should be inlined unless extracting it significantly improves readability of the caller.

### Comments

- Code should be self-explanatory. Comments are for **why**, not **what**.
- Do NOT add redundant comments explaining obvious code paths (e.g., `// set the name` above `name = value;`).
- Do NOT add generated XML doc comments（`/// <summary>` stubs） on private methods or trivial properties. Public API surface may carry concise XML docs where helpful.

### Cleanup Rule

- When adding new code, check whether the adjacent dead/retained code can be removed. If a feature fully replaces another, delete the old one — do not leave it as "retained".

### Proactive Optimization

- When encountering code that violates the above conventions（e.g. DTO mixed with logic, oversized method, missing `ConfigureAwait(false)`, dead code）, **proactively flag it and refactor it** — do not silently work around it.

## TranscriptVue（Vue 3 Frontend） Constraints

### Component Decomposition

- **Single responsibility**: one component per file. Do NOT pile multiple unrelated UI sections into the same `.vue` file. If a component exceeds ~300 lines, consider extracting sub-components into `components/<Feature>/`.
- Views（`views/`） orchestrate layout and delegate rendering to components（`components/`）. Views should be thin — move domain logic into composables（`composables/`）.
- Pure rendering logic（HTML string generation, markdown processing） lives in `renderers/`, not inside components.

### Style Isolation

- All component-level styles use `<style scoped>`. Global layout/reset/theme variables belong in `App.vue`'s unscoped `<style>` only.
- Do NOT mix scoped and unscoped `<style>` blocks in the same component file unless the unscoped block is exclusively for dynamic `v-html`-injected content that cannot be targeted by scoped selectors.
- The settings pages share the "Night Console" dark design system: tokens/keyframes live in `components/settings/settings-console.css` and are pulled into each component via `@import` inside its scoped style block (page root carries `sc-root` / `sc-stage`).

### Icons

- Icons come from `lucide-vue-next` components (`<Search :size="14" />`). Do NOT add emoji glyphs or hand-rolled inline SVG icon maps for new settings UI.

### Organization

- Vue file script layout: `<script setup>` → `<template>` → `<style scoped>`.
- Composables: one concern per composable. Composable files live in `composables/`, exported as `use<Feature>()`. Do not embed composable logic directly in component `<script setup>` beyond trivial local state.
- Async component loading: use `defineAsyncComponent` for route-level splitting（e.g. lazy-loaded settings panels）.

### Proactive Cleanup

- When a component grows too large, **proactively split it into smaller components**. When business logic appears inside a component file, **extract it into a composable**. When duplicate patterns appear across components, **extract into a shared composable or renderer util** — flag and refactor on sight.
