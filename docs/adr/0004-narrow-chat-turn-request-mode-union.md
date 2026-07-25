# Narrow ChatTurnRequest into a mode union

`ChatTurnRequest` is now an abstract shared turn intent — conversation, workspace, agent, and message
history — with two sealed subtypes: `DirectChatTurnRequest` (model profile, tool permission, approval handler)
and `CliChatTurnRequest` (CLI agent, model, reasoning effort). The dead `Mode` (`ConversationMode`) field is
gone; each subtype declares its own `AgentExecutionMode Mode`, and the dispatcher routes on that instead of on
`request.Agent.Mode`, so the request and the adapter it reaches can no longer disagree.

This removes the shallow eleven-field interface where each caller filled in fields the chosen runtime ignored:
Direct dropped the three CLI fields, CLI dropped the model profile, approval, and full history. Desktop now
builds only the resolved mode's shape — a Direct turn never resolves the CLI selection, and a CLI turn never
carries the provider model or approval inputs — so a mode's invariants stay in one place instead of leaking
across Core and Desktop. Tests construct the subtype they exercise rather than a request half of which is
discarded.

The terminal protocol (ADR-0001) and the CLI adapter seam (ADR-0002) are unchanged: adapters still receive the
base `ChatTurnRequest` through `IAgentRuntimeAdapter` and cast to their own subtype at entry, failing fast if
the dispatcher ever hands them the wrong shape.
