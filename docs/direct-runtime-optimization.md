# Direct Runtime Optimization

Scope: Direct execution from `ConversationTurnEngine` through capability resolution,
provider client construction, streaming, persistence, and transcript publication.

## Findings And Work Queue

| ID | Priority | Finding | Status |
| --- | --- | --- | --- |
| D01 | P1 | A failed capability resolution can leak acquired MCP leases. | Done |
| D02 | P1 | MCP health persistence can overwrite a concurrent user configuration change. | Done |
| D03 | P2 | MCP servers are connected and discovered serially before the model request. | Done |
| D04 | P2 | MCP pool entries for other workspaces are unnecessarily drained. | Done |
| D05 | P2 | Anthropic creates a new HTTP transport per Direct turn. | Done |
| D06 | P2 | Plugin and Skill manifests/content are reparsed on every Direct turn. | Done |
| D07 | P2 | Prompt reconstruction drops structured tool calls and tool results. | Done |
| D08 | P2 | Full conversation history is sent without a model-context budget. | Done |
| D09 | P2 | Whitespace-only streamed deltas are discarded. | Done |
| D10 | P2 | Streaming publication can wait up to 120 ms before the first visible update. | Done |
| D11 | P3 | Direct runtime setup and event translation are concentrated in oversized methods. | Done |
| D12 | P3 | Direct capability resolution passes mutable collections through many layers. | Done |

## Evidence And Acceptance Criteria

### D01 - MCP lease cleanup

`DirectTurnCapabilityResolver.ResolveCoreAsync` acquires MCP leases, then validates required
capabilities and creates subagent tools. Its outer catch currently releases Plugin leases only.
If a later validation or tool-name collision throws, every earlier MCP lease must be released.

Acceptance: a failed resolution releases every MCP lease exactly once; the normal path still
keeps all leases until `DirectTurnCapabilityLease.DisposeAsync`.

### D02 - Health write concurrency

`McpCapabilitySource.TryRecordHealthAsync` writes a complete snapshot obtained before connection
and discovery. A concurrent enable/disable or settings update must not be overwritten by a health
observation.

Acceptance: health fields update only the same configuration revision, and a stale observation is
ignored without failing the turn.

### D03 - MCP startup parallelism

`McpCapabilitySource.AddToolsAsync` awaits each server in an ordered loop. Configuration resolution,
connection, and tool discovery for independent servers should overlap within a bounded concurrency
limit. Tool ordering and diagnostics must remain deterministic.

Acceptance: total setup time is bounded by the slowest server plus scheduling overhead, failed
servers still degrade independently, and cancellation disposes all completed leases.

### D04 - MCP pool invalidation

`McpClientManager.MarkOlderEntriesDraining` drains every entry for a server whose pool key differs.
The pool key includes workspace path, so switching workspaces invalidates valid idle connections.

Acceptance: a configuration revision change drains old revisions; a workspace change does not drain
another workspace's valid entry, and idle expiry still works.

### D05 - Anthropic transport reuse

`AnthropicProviderAdapter` constructs `AnthropicClient` without the shared HTTP transport used by
OpenAI and Ollama adapters. The per-turn client must reuse pooled connections while preserving
explicit disposal ownership.

Acceptance: repeated turns use the same configured handler/pool and provider clients remain safe to
dispose at turn completion.

### D06 - Capability snapshot caching

Plugin manifests, Plugin instructions, and Skill files are read on every turn. Cache parsed static
content by package identity, version, content hash, and install path. A content-key change selects a
fresh entry; health and binding notifications do not invalidate static content.
Keep approval, workspace binding, and turn-specific tool wrappers out of the cache.

Acceptance: unchanged packages perform no repeated manifest/body reads, changed packages are loaded
again, and a failed read cannot poison later turns.

### D07/D08 - Prompt history and context budget

`DirectPromptComposer` rebuilds prior messages from `MarkdownContent`, which omits structured tool
calls/results. It also has no model-context budget or compaction policy. Preserve each recorded
call/result/answer sequence as one trimming unit. Reserve system instructions, the actual
`ChatOptions.Tools` definitions, continuation messages, and output space before selecting history.

Acceptance: follow-up turns retain required tool context in causal order, the complete request budget
stays within the configured model context limit, and trimming never creates an unmatched tool message.
Mandatory content or the newest history unit that cannot fit fails locally before provider streaming.
The budget uses a UTF-8-byte heuristic, not a provider tokenizer; without `context_window_tokens`
configured, full history is still sent and no context limit is enforced.

### D09 - Stream fidelity

`ConversationRuntimeState.ApplyAssistantDelta` uses `IsNullOrWhiteSpace`; newlines and indentation
are valid model output and must remain visible during streaming and interruption.

Acceptance: empty strings are ignored, all non-empty deltas including whitespace are preserved, and
terminal alignment keeps the same text and tool placement.

### D10 - First visible update

`TranscriptPublisher` coalesces stream changes at a 120 ms interval. Keep coalescing for sustained
token throughput, but publish the first visible text/thinking delta immediately and start the timer
only after that first publish.

Acceptance: the first text/thinking delta is not delayed by the coalescing interval; later updates
remain bounded and do not flood WebView2.

### D11/D12 - Maintainability

Keep the existing ownership boundaries, but split setup, content translation, terminal handling, and
resource cleanup into small private methods. Make capability assembly own all acquired leases in one
scope rather than relying on several mutable lists passed between sources.

Acceptance: no behavior change, one owner for every lease, and focused unit tests for each transition.

## Review Follow-up

The following review findings are tracked with permanent regression coverage below.

| ID | Related Work | Finding | Status |
| --- | --- | --- | --- |
| R01 | D01/D11 | Partial runtime initialization must release capabilities and any provider client. | Done |
| R02 | D06 | Shared content reads must be independent of each caller's cancellation. | Done |
| R03 | D07 | Replay tool calls, results, and following text in their recorded causal order. | Done |
| R04 | D08 | Reserve context space for the actual tool names, descriptions, and schemas. | Done |
| R05 | D08 | Reject mandatory input or the latest history unit when it cannot fit. | Done |
| R06 | D06 | MCP health notifications must not evict unchanged package content. | Done |

## Verification Log

| Date | Change | Verification |
| --- | --- | --- |
| 2026-09-06 | R01-R06 follow-up complete with permanent regression tests retained. | `dotnet test SelfClaw.Tests/SelfClaw.Tests.csproj --no-restore --verbosity minimal`: 648 passed, 0 failed, 0 skipped. Existing `TranscriptProjection.cs:268` nullable warning remains; no live-provider latency benchmark was run. |
| 2026-09-06 | R05: mandatory messages include the truncated-answer continuation prompt and Subagent completion batch. An oversized mandatory budget or newest history unit now throws an actionable `InvalidDataException` before provider streaming; older units are still trimmed as a contiguous tail. `DirectPromptBudget` is a separate data-only record. | Combined prompt/runtime/cache/resolver suites: 73 passed. Regression cases cover ASCII/CJK input, system instructions, explicit/default output reserves, a fully consumed window, completion batches, continuation trimming, and refusal before any provider call with ordered resource cleanup. |
| 2026-09-06 | R04: runtime passes its final `ChatOptions.Tools` to prompt composition. Names, descriptions, input JSON schemas, additional properties, and tool framing consume context space before history selection. | Combined suites: 73 passed. Large descriptions, schemas, and metadata each exceed a 400-token window and are rejected; ordinary tools reduce retained history; a runtime case with over 14000 estimated tool tokens never calls the provider. |
| 2026-09-06 | R03: assistant segments replay in ordinal order as call/result pairs followed by the text or later calls that depend on them. All replay messages from a stored assistant turn remain one trimming unit. | Combined suites: 73 passed, including result-before-answer ordering, sequential calls with or without intervening text, unordered input segments, and trimming the complete multi-call turn and its answer. |
| 2026-09-06 | R06: removed the cache's subscription to generic extension notifications. Content keys include the physical install path; live authorization and bindings remain evaluated on each turn. MCP health still notifies UI subscribers. | Cache/resolver suites: 27 passed, including two real resolver passes sharing a cached Skill while MCP health advances twice, changed version/hash, moved installation, and failed-read recovery. |
| 2026-09-06 | R02: shared cache reads use a cache-owned cancellation lifetime; callers cancel only their own `WaitAsync`. Failed reads evict their exact entry, so late failures cannot remove a replacement. Cache disposal cancels outstanding shared reads. | Cache suite: 8 passed, including cancellation of either waiter, continued reuse, shutdown, and late-failure eviction. |
| 2026-09-06 | R01: `SetupTurnAsync` owns its capability/provider leases until setup returns, and releases partial setup on exceptions or cancellation. Cleanup preserves the original error and releases the provider before capabilities. `DirectTurnSetup` is a separate data-only record. | Runtime suite: 17 passed, including 6 new provider-setup/cancellation/prompt-failure cases and existing successful cleanup coverage. |
| 2026-09-06 | Baseline captured before implementation. | 166 related tests passed; existing `TranscriptProjection.cs:268` nullable warning remains. |
| 2026-09-06 | D02: `IMcpServerRepository.UpdateMcpServerHealthAsync` writes only health fields conditioned on `config_revision`; `McpCapabilitySource` no longer upserts a full stale snapshot. Stale observations are dropped without failing the turn and without advancing the extension revision. | New sqlite test `Mcp_health_update_writes_only_health_fields_and_ignores_a_stale_revision`; resolver/recorder suites pass. |
| 2026-09-06 | D04: `McpClientManager` drains pool entries on a configuration-revision change only; the workspace path stays part of the pool key but no longer invalidates another workspace's entry. Idle expiry unchanged. | New test `AcquireAsync_WorkspaceChange_DoesNotDrainAnotherWorkspaceEntry`; existing revision-drain and pool-key tests pass. |
| 2026-09-06 | D09: `ConversationRuntimeState.ApplyAssistantDelta` drops only empty deltas (`IsNullOrEmpty`), preserving newlines and indentation during streaming; terminal alignment untouched. | New test `ApplyEventAsync_keeps_whitespace_only_text_deltas_and_ignores_empty_ones`. |
| 2026-09-06 | D05: `AiProviderHttpClientProvider.GetSharedStreamingHandler` caches one pooled handler per connection fingerprint; `AnthropicProviderAdapter` now builds each turn's `AnthropicClient` on a short-lived `HttpClient` wrapper over that shared handler (`disposeHandler: false`), so the SDK's dispose-its-HttpClient behavior can never tear down the pool. The DI registration injects the shared provider. | New tests `Shared_streaming_handler_is_reused_and_survives_a_turn_client_disposal`, `Provider_disposal_disposes_the_shared_handlers`, `CreateAnthropicClient_wraps_the_shared_pooled_handler_per_turn`. |
| 2026-09-06 | D10: `ConversationTurnRecorder` raises the transcript change with `immediate: true` for a turn's first text/thinking delta (`AgentTurnState.HasVisibleDelta`); `TranscriptPublisher.Publish` records every publish (immediate or coalesced) as the start of the next coalescing window, so later deltas stay on the 120 ms timer. | New test `ApplyEventAsync_publishes_the_first_visible_delta_immediately_and_coalesces_the_rest`; publisher suites pass. |
| 2026-09-06 | D06: new `CapabilityContentCache` singleton caches parsed Plugin manifests, Plugin instruction bodies, and Skill metadata keyed by package kind/id/version/content-hash (+file), is cleared on every `IExtensionStateChangeNotifier.StateChanged`, is size-capped, and never caches failed reads. `PluginCapabilitySource` and `SkillCapabilitySource` now read through it; approval, workspace binding, and turn-specific tool wrappers stay outside the cache. | New `CapabilityContentCacheTests` (5 tests); full suite 604/604 green. |
| 2026-09-06 | D12: new `DirectTurnLeaseScope` is the single owner of every Plugin-version and MCP-client lease taken while assembling a turn's capabilities. Sources hand leases to the scope as they are taken (`Add` returns `false` on a concurrently disposed scope so the acquirer disposes its own lease); `DirectTurnCapabilityLease` disposes the scope via `leases.DisposeAsync`. `McpCapabilities` no longer carries a lease list. | Covered by D01/D03 lease tests; full suite green. |
| 2026-09-06 | D01: `DirectTurnCapabilityResolver.ResolveAsync` creates the lease scope before resolution and disposes it in its catch - so a failure after MCP leases were acquired (required-capability validation, tool-name collision) releases every MCP and Plugin lease exactly once. | New test `ResolveAsync_a_late_resolution_failure_releases_every_acquired_mcp_lease` (2 leases released after a post-connect failure); existing collision test asserts single release. |
| 2026-09-06 | D03: `McpCapabilitySource` now resolves, connects to, and discovers tools of independent servers concurrently via `Parallel.ForEachAsync` bounded at `MaximumConcurrentServers = 4`. Per-server results are buffered and merged in server order (deterministic tool ordering, diagnostics, and collision detection); state-change notifications fire serially in the merge; failed servers still degrade independently and cancellation disposes completed leases through the scope. | New test `ResolveAsync_connects_independent_mcp_servers_with_bounded_parallelism` (peak concurrency = 4 across 6 servers); resolver suite stress-passed 5x, full suite 606/606. |
| 2026-09-06 | D07: `DirectChatTurnRequest` now carries `ToolExecutions` (interactive engine, Subagent child turns, and parent continuations all pass their recorded tool runs). `DirectPromptComposer` replays assistant history from its structured blocks: Text segments become `TextContent`, ToolCall segments are rebuilt as `FunctionCallContent` + a `ChatRole.Tool` `FunctionResultContent` pair (failed runs replay with the error exception set). Thinking blocks are deliberately not replayed (stored reasoning has no provider signature, so Anthropic rejects it); messages without segments fall back to markdown; tool calls without a matching run are skipped, never orphaned. | New `DirectPromptComposerTests` (8 tests) and runtime-level test `StreamTurnAsync_replays_prior_tool_calls_as_structured_messages`; full suite 615/615. |
| 2026-09-06 | D08: new `context_window_tokens` model option (`AiChatOptions.ContextWindowTokensKey`) declares a model's context window. `DirectPromptComposer` reserves the system prompt, the Subagent completion batch, and the turn's output cap (`MaxOutputTokens`, defaulting the reserve to 4096), then keeps a contiguous tail of whole history units that fits the remainder - always including the latest message. Without the option the full history is sent as before. Token estimates use UTF-8 bytes over 3 (conservative for English, accurate for CJK). | Covered by `BuildMessages_trims_old_history_within_the_context_budget_and_keeps_the_latest_turn` and `BuildMessages_keeps_tool_call_result_pairs_together_when_trimming`; full suite 615/615. |
| 2026-09-06 | D11: `DirectAgentChatRuntime.ProduceEventsAsync` (was ~240 lines) is split into focused private methods - `SetupTurnAsync` (capabilities + provider client + run-start events + prompt), `StreamResponseAsync` (streaming loop), `WriteTerminalOutcome` (finish-reason terminal events), `DisposeSetupAsync` (resource cleanup) - with the mutable translation state owned by the private `TurnOutputStream` (`TranslateUpdate`, `ReportUsage`). Ownership boundaries unchanged; the runtime suite asserts identical event sequences, usage, and disposal. | Full suite 615/615 green with no test changes to existing runtime assertions. |
