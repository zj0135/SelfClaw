# Deepen the CLI adapter module

`CliAgentChatRuntime` remains the internal CLI adapter behind the runtime dispatcher. Its external
interface stays one streaming turn method, while command construction, stdin encoding, parser selection,
and session argument rules move behind one small `ICliAgentAdapter.PrepareTurn` seam per CLI.

Session persistence is common orchestration: the runtime loads the stored id before preparation and saves
any non-empty id reported by `RunStartedEvent`. This removes the shallow `ResumeStrategy` and
`CliSessionResolver` modules without changing the storage key or the terminal protocol established by
ADR-0001.

Codex and OpenCode parsers are separate implementations because their JSON event protocols vary. The
shared process host, command resolver, cancellation propagation, and dispatcher terminal discipline remain
outside the per-CLI adapters.
