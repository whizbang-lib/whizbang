# Priority, composite integrity, and the open issues (one PR)

Branch `feat/priority-and-composite-integrity` off develop (2305f3b08). One pull request, many commits, one
increment per commit group. Every increment ships red first, reaches 100% coverage of the lines it adds, leaves
zero open Sonar findings, uses no reflection (AOT), renders every type name through the shared helpers
(`TypeNameFormatter`, `EventTypeMatchingHelper`, `EnvelopeTypeNameHelper`), and updates the docs site with
`<docs>` and `<tests>` links both ways.

## Scope

| # | issue | increment |
|---|---|---|
| 1 | #737 | a composite is expanded and committed as one transactional step; a re-offered composite whose commit is pending is never expanded again; child ids are deterministic (composite id + inner identity) as the last line of defense |
| 2 | #736 | classification is positive: a composite child is an event unless the catalog says otherwise, and a child the consumer has no subscription for is dropped at expansion, never stored |
| 3 | #738 | passive meters: composites received, expansions, children created, children discarded (unsubscribed), children refused or dead-lettered; collective events received, applied, skipped |
| 4 | #740 | the handler commit channel is bounded (rows and bytes) with back-pressure on dispatch; queued-but-uncommitted children count toward the outstanding figure the budget reads; a depth meter |
| 5 | #739 | `wh_inbox.source_service_id` holds the producing service's id, taken from the envelope, rendered by the shared helper |
| 6 | #731 | `wh_active_streams.lease_expiry` is set when a stream is leased and renewed or cleared with the rows, so the stream-level ownership guards are live (migration 148) |
| 7 | #741 | the outstanding budget never leases more than the measured drain rate can complete inside a lease (`leased <= rate * lease_seconds * k`), whatever the ceiling; an unmeasured consumer starts at a fraction of the ceiling |
| 8 | #734 | inbox completions queued by the Service Bus consumer worker are drained by the flush helper's correct path |
| 9 | proposal step 1 | priority: one integer on the envelope, a `priority` column on inbox/outbox/perspective events, three per-bucket pending counters on the stream row, stamping from dispatch context, inheritance, the producer/receive/batch hooks with their default implementations, meters (migrations 148/149) |
| 10 | proposal step 2 | the tag surfaces (`DeclarePriority`, `ClassifyNamespace`, `ClassifyType`, `Classify`, the stream fold declaration) as sugar over the hooks; the default "accept the declared number" policy; positive classification |
| 11 | proposal step 3 | bucket-aware claim: the fold over the counters, deficit round robin with a floor per bucket, wait-target aging within and across bands, in `claim_work`, `claim_orphaned_inbox`, and the perspective claim; the per-category cap stays the same accounting |
| 12 | proposal step 4 | the work coordinator gate and the pinned pool reserve a share for the interactive bucket |
| 13 | proposal step 6 | notification tags bound to coalescing by default, if the binding is a policy over the existing mechanism |
| 14 | proposal steps 5 and 7 | assessed at the end: the transport lane by bucket rides on the traffic-classes routing and tenant fairness is the proposal's own second phase; either ships here only if it fits without a second design round, otherwise it is a follow-up issue with the reason written down |

Order: 1, 2, 3 (the composite pipeline is where the measured damage is), 4, 5, 6, 7, 8, then 9 to 13.

## How each increment is proven

- Red first: the failing test names the behavior; the green commit references it; a migration increment runs its SQL
  tests with the migration held out first, then restored.
- Coverage: `pwsh scripts/Find-UncoveredNewLines.ps1` against the CI artifacts, 0 lines; `pwsh scripts/Invoke-PrHealth.ps1`
  before every push.
- Tests never wait on real time: `FakeTimeProvider`, throwing fakes, canceled tokens. Worker tests follow
  `ai-docs/flaky-tests.md` pattern 7 (`StartAsync` proves scheduling only).
- Builds and test runs one project at a time (shared machine); Release build before pushing.

## Docs (whizbang-lib.github.io, branch `docs/priority-and-composite-integrity`)

- Move the proposal to drafts and then to the released tree as the increments land; record the decisions.
- Pages to update: composite fan-out and inbox dispatch, claim loop and acquisition bound, message cascade
  and emission identity, work-coordinator configuration reference (new options), observability meter list,
  notifications, the Service Bus consumer, contributors pages if a convention changes.
- Every C# example carries `tests=[...]`; every page's frontmatter carries `codeReferences` and `testReferences`.

## Progress

- #731: red confirmed (5 of 6 fail: NULL stream lease; steal took an owned stream), migration 148 placed
  (three acquisition functions reproduced from their last words plus the stream lease), green 6 of 6.
  Found while writing the docs: `renew_leases` (029) never touched the stream lease, so a long handler's
  stream lease would lapse at the original row lease while its rows were still being renewed and a sibling
  could take the stream's next row mid-handler. Two more red tests (renewal extends the owner's stream lease;
  a renewal by a different row holder leaves a stream assigned to someone else alone); 148 gains a
  `renew_leases` redefinition (rows first, then streams, same lock order as acquisition; the returned count
  stays the category rows).
- #737 #736 #738 #740 #739 #734 #741 and proposal step 1 (types, hooks, envelope field): red tests written,
  API skeletons compile, Core red run queued behind the EF build (one build at a time).
- Docs branch `docs/priority-and-composite-integrity`: composite-events page (transactional expansion,
  deterministic child ids, unsubscribed children, meters); metrics (composites, collectives, commit-queue gauge);
  lease semantics (stream leases); claim backpressure (lease-aware budget, default on); inbox pattern (source
  identity); work coordinator (queued inbox completions); multi-instance rule badge; message-priority page
  (step 1: number, bucket, hooks, ambient parent, decisions). Still to write: configuration reference for new
  options, migrations page for 148/149, priority page sections for steps 2 to 4 and 6 as they land.
- Test analyzer rules met on the way: no `Assert.That(constant)`, no `Span<T>` across an await, CA1859 on
  helper parameter types, and every fake implements the whole interface (`ITransport.IsInitialized`).

- Green pass one (composite pipeline, budget, flush helper, source identity, commit-queue gauge, collective
  meters, priority step 1 types and hooks): implemented; the combined build and run is in progress.
- Priority step 1, second half (red tests written, not yet run): the dispatcher declares through the chain
  (`DispatcherPriorityStampingTests`), both consumers classify through the chain and the row carries the
  answer (`ConsumerPriorityClassificationTests`), the dispatch worker enters the row's number as the ambient
  parent (`InboxDispatchWorkerPriorityContextTests`), and migration 149 stores the number on inbox, outbox
  and perspective rows, returns it from the inbox fetch, and carries it into perspective work
  (`MessagePrioritySqlTests`). `Priority` properties exist on `OutboxMessage`, `InboxMessage`,
  `InboxBatchRow` and `InboxWork` as skeletons.

- [ ] 1 #737  - [ ] 2 #736  - [ ] 3 #738  - [ ] 4 #740  - [ ] 5 #739  - [~] 6 #731  - [ ] 7 #741  - [ ] 8 #734
- [ ] 9 step 1  - [ ] 10 step 2  - [ ] 11 step 3  - [ ] 12 step 4  - [ ] 13 step 6  - [ ] 14 steps 5 and 7 assessed
- [ ] docs branch and PR  - [ ] PR open, `Invoke-PrHealth` clean
