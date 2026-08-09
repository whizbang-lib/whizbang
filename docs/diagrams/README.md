# Whizbang Event Processing Visualizations

Mermaid-based architecture diagrams covering the complete event processing pipeline. All diagrams render natively on GitHub.

## Diagrams

| # | Diagram | What It Shows |
|---|---------|---------------|
| 01 | [Message Type Hierarchy](01-message-type-hierarchy.md) | IMessage → ICommand / IEvent / IQuery type tree, receptor patterns |
| 02 | [Core Dispatch Flow](02-core-dispatch-flow.md) | SendAsync / PublishAsync / LocalInvokeAsync routing through DispatchModes |
| 03 | [Lifecycle Stages Pipeline](03-lifecycle-stages-pipeline.md) | 24 lifecycle stages across Dispatcher → Outbox → Inbox → Perspective workers |
| 04 | [Inbox / Outbox Pattern](04-inbox-outbox-pattern.md) | Cross-service reliable messaging with at-least-once / exactly-once guarantees |
| 05 | [Event Sourcing & Perspectives](05-event-sourcing-perspectives.md) | Append-only event store → perspective projections → queryable read models |
| 06 | [Message Envelope Journey](06-message-envelope-journey.md) | Hop tracking, causation chains, security deltas, W3C Trace Context |
| 07 | [End-to-End Flow](07-end-to-end-flow.md) | Complete command → event → projection → query lifecycle |

## Reading Order

**New to Whizbang?** Start with **01** for the type system, then **07** for the big picture, then dive into details.

**Debugging a flow?** Jump to **07** (end-to-end) to find which phase your issue is in, then read the relevant detail diagram.

**Operations / monitoring?** Focus on **03** (lifecycle stages), **04** (inbox/outbox), and **06** (envelope tracing).

## Animated Visualizations

Interactive step-by-step animations for the most temporal flows. Open the HTML files directly in a browser — no build step or server required.

| Animation | What It Shows |
|-----------|---------------|
| [End-to-End Flow](animations/07-end-to-end-flow.html) | 18-step walkthrough: command dispatch through event persistence, cross-service delivery, perspective projection, and query |
| [Inbox / Outbox Pattern](animations/04-inbox-outbox-pattern.html) | 14-step animation: transactional outbox write, transport delivery, inbox deduplication, with live record field updates |
| [Message Envelope Journey](animations/06-message-envelope-journey.html) | 12-step hop-by-hop trace: envelope inspector showing MessageHop fields, ScopeDelta accumulation, and causation chains |
| [ProcessWorkBatch Lifecycle](animations/process-work-batch-lifecycle.html) | 20-step animation: C# accumulation, flush strategy, PostgreSQL 7-phase execution, result distribution, feedback loop |
| [Perspective Runner Replay](animations/08-perspective-runner-replay.html) | 13 steps: RunAsync, RewindAndRunAsync (late event), BootstrapSnapshotAsync — snapshot restore and replay |
| [Lifecycle Coordinator WhenAll](animations/09-lifecycle-coordinator-whenall.html) | 10 steps: parallel path tracking, ExpectCompletionsFrom, SignalSegmentComplete, PostLifecycle firing |
| [Policy Routing Engine](animations/10-policy-routing-engine.html) | 7 steps: predicate evaluation (first-match-wins), topic/stream/partition assignment, PolicyDecisionTrail |
| [Perspective Sync (AppendAndWait)](animations/11-perspective-sync-append-wait.html) | 10 steps: request-response over event sourcing, AwaitPerspectiveSync polling, read-your-writes consistency |
| [Time-Travel Debugging](animations/12-time-travel-debugging-rewind.html) | 8 steps: late event detection, snapshot restore, UUID7-ordered replay, model correction |
| [Source Generator Pipeline](animations/13-source-generator-pipeline.html) | 6 steps: syntactic filtering funnel (95% eliminated), semantic analysis, value records, template codegen |
| [Tag Hook Processing](animations/14-tag-hook-processing.html) | 7 steps: tag discovery, priority-sorted hook chain, payload mutation (audit → SignalR → encryption) |
| [Consistent Hashing Partitions](animations/15-consistent-hashing-partitions.html) | 8 steps: virtual partitions, heartbeat/rank, instance failure, automatic rebalancing, self-healing |

Controls: Play/Pause (Space), Step Forward/Back (Arrow keys), Speed (0.5x-4x), clickable timeline.

See [animations/README.md](animations/README.md) for details.

## Rendering

These diagrams use [Mermaid](https://mermaid.js.org/) syntax and render automatically on:
- GitHub (markdown preview)
- VS Code (with Mermaid extension)
- Any Mermaid-compatible viewer

To render locally: `npx @mermaid-js/mermaid-cli -i diagram.md -o diagram.svg`
