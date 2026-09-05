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
  → WebViewMessageRouter.RouteAsync()
    ├─ settings request → feature bridge → correlated WebView response
    ├─ shell command → MainWindow applies window-only behavior
    └─ conversation intent → MainWindowViewModel
      → SubmitPromptAsync() captures the current UI selection and provisions a managed Git worktree when requested
      → ConversationTurnEngine.ExecuteAsync()
        → admits the turn and persists the conversation + user message
        → builds the Direct/CLI ChatTurnRequest
        → DispatchingAgentChatRuntime.StreamTurnAsync()
          ├─ Mode=Direct → DirectAgentChatRuntime
          │   → AiChatClientFactory (selected/default model profile + protected credential)
          │   → provider IChatClient + WorkspaceAgentToolset + desktop approval
          │   → M.E.AI updates → AgentStreamEvents
          └─ Mode=Cli → CliAgentChatRuntime
              → CliSessionResolver + CliAgentRegistry
              → CliCommandResolver → CliAgentProcessHost (subprocess)
              → stdout JSONL → ClaudeStreamJsonParser / JsonEventStreamParser → AgentStreamEvents
        → DesktopTurnFinalizer persists the terminal state
  → ConversationSessionCoordinator → ITranscriptChangeSink → TranscriptPublisher
    → TranscriptRenderState → WebViewHostChannel replay → Vue renders

Background SubagentDeliveryDispatcher (when durable child results are pending)
  → parent-priority admission/lease/coalescing
  → detached Direct continuation with transient completion batch
  → atomic parent terminal + Delivered, bounded retry or DeadLetter notification
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
- `Agents/Subagents/Persistence/SqliteSubagentDeliveryRepository.cs` — snapshot-aware mailbox lease, heartbeat, atomic resolution and expired-lease recovery
- `Services/Subagents/SubagentDeliveryDispatcher.cs` — coalescing, user-priority continuation dispatch and restart recovery
- `Services/Subagents/SubagentContinuationExecutor.cs` — detached continuation runtime and lease heartbeat
- `Services/Subagents/SubagentTaskCoordinator.cs` — durable child lifecycle, cancellation and bounded delete wait

### Desktop ViewModel

`MainWindowViewModel` owns UI selection, navigation state, and shell projection; deeper workflow modules own execution and publication:
- `MainWindowViewModel.cs` — prompt snapshots, conversation navigation, workspace selection, and transcript request construction
- `MainWindowViewModel.Agents.cs` — preserves each `DesktopAgentDefinition`'s Direct/CLI mode in `AgentRuntimeDefinition`
- `ConversationTurnEngine.cs` — turn admission, conversation/message persistence, request construction, runtime dispatch, event reduction, terminal finalization, and completion notification
- `ConversationSessionCoordinator.cs` — running conversation state, cancellation, selected transcript synchronization, and direct presentation signaling
- `TranscriptPublisher.cs` — dispatcher marshaling, stream coalescing, projection dedupe, invalidation, and WebView replay publication
- `WebViewMessageRouter.cs` — frontend request routing, bridge responses, shell intents, and host-only commands
- `ConversationTurnEngine.cs` — shared admission gate, deletion tombstones and detached continuation admission

### Agent definitions

`DesktopAgentDefinitionService` loads and atomically updates `.md` files from `{AppData}\agents\`. Built-in agent id: `build`.
Agent markdown supports front matter: name, description, mode, tools, plugins, skills, mcpServers, subagents. Direct turns resolve those ids against the enabled extension catalog; CLI turns keep their existing subprocess behavior.
Subagent definitions live in `{AppData}\subagents\` via `SubagentDefinitionCatalog` (name, description, modelProfileId, tools, plugins, skills, mcpServers, maxRunSeconds); `Save()` writes them atomically with the same strict validation as load.
The 代理助手 settings page talks to `AgentSettingsBridge` (prefix `agents/`): `get-state`, `save-agent`, `set-binding`, `set-subagent-binding`, `save-subagent`, `set-subagent-extension-binding`. Every mutation raises `AgentsChanged` (router reloads the VM agent cache) and advances the shared extension revision.

### 插件面板 (Plugin panels)

A Plugin package may contribute right-hand UI panels through `contributes.panels` in `plugin.json`
(alongside `directInstructions` / `skills` / `mcpServers`). Panels render as browser-style tabs in a Vue
column, not in WPF.

Each panel is served from its own origin, `https://<plugin-id>.plugin.selfclaw.local`. That is load
bearing: the distinct origin gives every Plugin its own renderer process, its own storage partition, and
an `event.origin` the shell treats as unforgeable identity. A Plugin whose id is not a legal DNS label is
rejected at install time rather than failing when a user first opens the tab.

Three layers, outermost first:
- `WebViewMessageRouter.RouteAsync` drops any message whose `CoreWebView2WebMessageReceivedEventArgs.Source`
  is not the application origin, before `type` is read. This is the load-bearing check, and it holds
  whether or not WebView2 exposes `chrome.webview` inside iframes.
- `PluginPanelHost.vue` / `usePluginPanels.js` own the iframes and derive panel identity from
  `event.origin` plus an `event.source === iframe.contentWindow` match. A `pluginId` in a payload is never
  trusted.
- The panel runs sandboxed under a host-issued CSP. `allow-same-origin` is required (without it the origin
  is opaque and both identity and storage are lost); it is safe here only because the plugin host differs
  from the app host.

Host-side pieces:
- `Services/Plugins/PluginPanelHostController.cs` — virtual host mappings, `WebResourceRequested` serving
  with CSP/nosniff headers, version leases, `plugin-host/*` messages, tab persistence; implements
  `IPluginPanelSessionRegistry` so disable/delete evicts panels before draining a version directory
- `Services/Plugins/PluginPanelBridge.cs` — `plugin-host/api` ops; resolves permissions from host state
  (never from the payload) and pins the workspace root to the current selection
- `Services/Plugins/PluginPanelContextPublisher.cs` — the only producer of `PluginPanelContext`. It both
  answers `getContext()` and pushes `plugin-host/context`, so the pulled and pushed shapes cannot drift.
  Captured by `MainWindowViewModel.CaptureContext()`; deduplicated by record value, except on panel open
- `Assets/plugin-sdk.js` — injected into every document via `AddScriptToExecuteOnDocumentCreatedAsync`

Permissions are a disclosure list, so unknown bare tokens stay legal. `network.fetch:<origin>` is parsed
strictly and widens only that panel's `connect-src`; a Plugin declaring none is fully offline. Panel
definitions live in the existing `extension_packages.manifest_json` and open tabs in
`desktop-settings.json`, so this added no schema version.

### Tool approval

Direct `write_file` and `run_shell_command` calls use `DesktopToolApprovalHandler` when the conversation is in `RequireApproval` mode. A visible window shows a WPF Yes/No prompt; a hidden/minimized window sends a Windows toast with Confirm/Cancel actions. Pending approvals default to rejection on timeout, subscriber failure, or window close. CLI mode continues to use the CLI's own permission policy.

### WPF shell

- `MainWindow.xaml` — custom chrome, title bar buttons, single WebView2 host
- `LeftSidebar.xaml` — sidebar with Settings entry
- Settings view: AI 提供商, 编程助手, 代理助手, 插件, and 宠物 are connected to the desktop host; remaining pages are frontend placeholders/mock
- The right-hand plugin panel column lives in the Vue app, not in WPF (see 插件面板)

### DI Registration

Infrastructure (`ServiceCollectionExtensions.AddSelfClawInfrastructure()`):
- Repositories: `SqliteConversationRepository`, `SqliteAiProviderRepository`, `SqliteExtensionRepository`
- Subagents: `SqliteSubagentTaskRepository` (`ISubagentTaskStore`/`ISubagentTaskExecutionStore`) and `SqliteSubagentDeliveryRepository` (`ISubagentDeliveryStore`)
- AI providers: catalog/registry, provider adapters, `AiProviderHttpClientProvider`, `AiProviderSettingsService`, `AiChatClientFactory`
- Runtimes: CLI process/session services, `CliAgentChatRuntime`, `DirectAgentChatRuntime`, `DispatchingAgentChatRuntime` (as `IAgentChatRuntime`)
- Extensions: `ExtensionCatalog`, `ExtensionPackageInstaller`, `ExtensionSettingsService`, `ExtensionStateChangeNotifier`, `DirectTurnCapabilityResolver` plus its `SkillCapabilitySource` / `PluginCapabilitySource` / `McpCapabilitySource`, Skill readers/runtime tools
- MCP: configuration/transport factories, pooled `McpClientManager`, SDK connection factory, `McpToolAdapter`
- Tools: `WorkspaceToolService`, `WorkspaceAgentToolset`
- Security: `DpapiSecretProtector`

Desktop (`App.xaml.cs`):
- `DesktopAgentDefinitionService`, `SubagentDefinitionCatalog`, `ExtensionSettingsBridge`, `AgentSettingsBridge`, `DesktopSettingsJsonStore`, `DesktopToolApprovalHandler`, `DesktopNotificationService`,
  `DesktopNotificationActivationService`, `ProgrammingAssistantSettingsService`, `AiProviderSettingsBridge`,
  `ConversationTurnEngine`, `ConversationSessionCoordinator`, `TranscriptPublisher`, `WebViewMessageRouter`,
  `PluginPanelHostController` (also `IPluginPanelSessionRegistry`), `PluginPanelContextPublisher`, `PluginPanelBridge`,
  `SubagentTaskCoordinator` (`ISubagentTaskCoordinator` and `ISubagentConversationLifecycle`), `SubagentTaskBackgroundHost`, and `SubagentDeliveryDispatcher` hosted services,
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

Schema version: **25** (in `SqliteDatabase.cs`). Tables: `ai_provider_connections`, `ai_model_profiles`, `ai_model_profile_selections`, `extension_packages`, `mcp_server_configs`, `workspace_roots`, `git_repositories`, `git_checkouts`, `conversations`, `messages`, `message_segments`, `message_attachments`, `tool_runs`, `cli_agent_sessions`, `subagent_tasks`, and `subagent_deliveries`. Schema v25 structures assistant content into `message_segments` blocks (Text/Thinking/ToolCall with ordinal placement) and rebuilds `tool_runs` without the retired `after_segment_index` column; legacy assistant rows are not migrated. The v22→v23 migration atomically rebuilds `conversations` when legacy `profile_id`, `kind`, or `parent_conversation_id` columns require it, preserves existing data, and defaults old rows to interactive ownership. Schema v24 adds repository identity and checkout ownership without changing the physical Workspace Root execution contract. Subagent deliveries use snapshot-aware FIFO batching, 45-second leases with 15-second heartbeat, and atomic Delivered/DeadLetter resolution.

### Image attachments

Persisted to `{AppData}\attachments\{convId}\{msgId}\`. Max 6 images, 10MB each, 30MB total. Served to WebView2 via `https://attachments.selfclaw.local/{path}`.

### Transient state

`TranscriptRenderState` is the DTO published to the Vue frontend. Assistant content is structured as `MessageSegmentRecord` blocks (Text/Thinking/ToolCall); the block order is the transcript order, and tool cards render where their ToolCall block sits. `TerminalBlockAligner` maps terminal FinalText onto the streamed blocks once per turn.
Continuation turns use detached `ConversationRuntimeState`; their transient completion batch is prompt-only and is never persisted as a parent user message or streamed into the selected transcript before atomic terminal commit.

### Conversation deletion

Deleting an interactive parent first marks a deletion tombstone, stops its active turn, cancels and bounded-waits all queued/running child tasks through `ISubagentConversationLifecycle`, and only then applies SQLite cascade. A timeout aborts deletion. Conversation list/navigation also defensively accept only `ConversationKind.Interactive`, so crafted child rows cannot enter the normal Vue workflow.

## Dead / Retained Code (NOT active)

- **Feishu channel**: fully implemented but never registered in DI
- **Plan mode**: removed; `AgentExecutionMode.Direct` and `AgentExecutionMode.Cli` are both active
- **Channel conversations**: data model retained but VM filters them out
- **Settings pages**: AI 提供商, 编程助手, 代理助手, 插件, and 宠物 are wired to the host; the remaining settings pages are frontend mock
- **Legacy provider profiles**: `ProviderProfile`, `IProfileRepository`, the `profiles` table, and `ChatTurnRequest.Profile/ApiKey` were removed; Direct turns use `ModelProfileId`

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
- The settings pages share the light "Console" design system: tokens/keyframes plus the shared page scaffold (`sc-page` / `sc-page-head` / `sc-page-body` — fixed gray header over a white scrolling body) live in `components/settings/settings-console.css` and are pulled into each component via `@import` inside its scoped style block (page root carries `sc-root` / `sc-stage`).

### Icons

- Icons come from `lucide-vue-next` components (`<Search :size="14" />`). Do NOT add emoji glyphs or hand-rolled inline SVG icon maps for new settings UI.

### Design Standards

- **图标**：统一使用 Lucide 图标，界面全程禁止使用表情符号。
- **设计标准**：对标 Awwwards 顶级网站水准，达到 Awwwards、FWA、CSS Design Awards 每日最佳网站同等设计品质。
- **创意自由度**：将浏览器视作交互式艺术画布，跳出传统布局框架，追求先锋视觉风格、实验性排版、流畅物理动效、极具冲击力的文字版式。
- **沉浸式体验**：融合代码、高级渲染逻辑，打造统一完整的精品页面，做出突破常规 UI 认知、令人惊艳的数字交互体验。

### Organization

- Vue file script layout: `<script setup>` → `<template>` → `<style scoped>`.
- Composables: one concern per composable. Composable files live in `composables/`, exported as `use<Feature>()`. Do not embed composable logic directly in component `<script setup>` beyond trivial local state.
- Async component loading: use `defineAsyncComponent` for route-level splitting（e.g. lazy-loaded settings panels）.

### Proactive Cleanup

- When a component grows too large, **proactively split it into smaller components**. When business logic appears inside a component file, **extract it into a composable**. When duplicate patterns appear across components, **extract into a shared composable or renderer util** — flag and refactor on sight.
