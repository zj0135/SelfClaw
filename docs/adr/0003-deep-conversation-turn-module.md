# Deep conversation turn module

The reduction of one conversation turn — assistant text / thinking deltas, inline tool runs, usage, and the
success / failure / cancellation terminal — moves out of the WPF `MainWindowViewModel` into a `ConversationTurnEngine`
that touches no `System.Windows` type. The engine reduces the unified `AgentStreamEvent` stream into the transcript
projection held by `ConversationRuntimeState` (promoted to a top-level type alongside `AgentTurnState`), persists tool
runs as they arrive, and finalizes the assistant message and any pending tools atomically through `DesktopTurnFinalizer`,
preserving the terminal discipline of ADR-0001.

The ViewModel keeps only what is genuinely WPF: it collects user input, drives the `StreamTurnAsync` loop, catches
cancellation / failure to call the engine's interrupt finalizer, and owns snapshot publishing. The engine signals a
transcript change through `ConversationRuntimeState.RaiseTranscriptChanged(immediate)` — throttled for streaming deltas,
immediate for the terminal snapshot — so the ViewModel decides when to flush to Vue without the engine knowing about the
dispatcher or the throttle.

Direct and CLI turns share this projection because both arrive as the same events, so the transcript reduction is proven
once. The event sequence is verified at the engine's own surface — RunStarted → deltas → tool start/complete → terminal,
plus cancelled / failed interrupts — without starting a window.
