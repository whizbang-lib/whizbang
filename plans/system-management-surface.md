# Plan: System Management Surface (execution hand-off)

**Proposal (read first):** docs repo, branch `docs/dlq-integrity-and-meter-subscription`,
`src/assets/docs/proposals/system-management-surface.md` — defines the four layers
(per-service queries, system commands, fleet scatter-gather, host adapters), the full
data/action inventory, the daily-operations acceptance walkthrough, phasing P1–P5, and five
open questions needing the maintainer's decision before P2.

**Starting state:** branch `feat/dlq-system-commands` (this branch), commit `5f01230fe` — P1
groundwork, COMPILES but NOT test-covered yet:
- `ReleaseHeldDeadLettersCommand` + `RequestDeadLetterScanCommand` in
  `src/Whizbang.Core/Commands/System/SystemCommands.cs` (PinnedId'd, IControlPlaneMessage,
  registered in `ControlPlaneTypeRegistry`)
- `DeadLetterStatusSummary`/`CampaignStatus` records + `GetStatusSummaryAsync` on
  `IDeadLetterRecoveryService` (`src/Whizbang.Core/Messaging/IDeadLetterRecoveryService.cs`)
- EFCore implementation in `EFCoreDeadLetterRecoveryService` (with `_tbl` schema helper)

**P1 remaining (finish first, one PR):**
1. RED tests before any further impl — the repo's bar is OBSERVED red (revert-prove if code
   preceded the test):
   - `SystemCommandsTests`: both commands create/serialize; control-plane registry contains them.
   - New `DeadLetterOperationsReceptorTests` (EFCore.Postgres.Tests): receptor releases the
     named cohort; null fingerprint fans out over `ListHeldCohortsAsync`; scan command calls
     `ResetForGenerationAsync(gen ?? current, 0)`; use a capturing fake recovery service.
   - `DlqStatusSummarySqlTests`: seed rows in each status + campaigns; summary counts match.
   - AspNet: `GET /whizbang/dlq/status` returns the summary (extend
     `DeadLetterOperatorEndpointsTests`; JSON context needs the new records registered).
2. Implement: `DeadLetterOperationsReceptor` + `DeadLetterOperationsReceptorRegistrar` in
   `Whizbang.Data.EFCore.Postgres` — copy `RebuildCommandReceptorRegistrar` EXACTLY (three
   lifecycle stages, optional registry, hosted-service registration in
   `PostgresDriverExtensions` next to the existing registrars).
3. Fakes ripple: `IDeadLetterRecoveryService` has ~5 fakes (Core.Tests worker/campaign tests,
   AspNet tests) — each needs `GetStatusSummaryAsync`.
4. Docs: canary-recovery page (commands + status endpoint), configuration reference if any
   knob appears; `<docs>`/`<tests>` tags on all new types.

**Then P2–P5 per the proposal.** P2 (fleet scatter-gather) BLOCKS on open questions 1/2/4 —
get answers before building it.

**Quality bar (non-negotiable, from the repo's standing rules):** RED observed before GREEN;
zero reflection/AOT (binder-generator patterns, no generic Bind, no JsonNode); en-US; dotnet
format; migration lint if SQL changes; never name consumers/environments; PR to develop,
never push develop; one issue/phase per commit where natural.

**Verify environment quirk:** local shell has DOTNET_ENVIRONMENT=Development →
ValidateOnBuild runs locally (not CI); sample fixtures may need
ServiceRegistrationCallbacks.Dispatcher=null before AddWhizbang.
