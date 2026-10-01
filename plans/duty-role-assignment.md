# Duty role assignment: the advisory lock decides the vote, the row is the role

Status: phases 1 and 2 delivered on `feat/966-role-assignment`, opt-in, shipping as one PR (see sections 2.1 and 3.1). Issue #966. Design and requirement mapping:
docs site `proposals/duty-role-assignment`. This file is the build plan. It records what each phase
ships, how it is verified, and the decisions made along the way.

## 1. Inventory: every election and exclusivity mechanism today

Checked by `git grep` over `src/` for `pg_try_advisory`, `pg_advisory_*`, `IDutyElector`, `leader` and
`exactly one instance`. Only the first two rows are elections in the sense of #966, where one
instance holds a role for a tenure. The rest are listed so that nobody moves them by mistake.

| Mechanism | Where | Kind | Moves to role assignment? |
|---|---|---|---|
| Duty election (`migrator`, `maintainer`) | `PgDutyElector`, callers `MigratorDutyStaging` (generated schema init) and `StartupPipelineRunner` (`TableRewriteStartupStep` requires `maintainer`) | Session advisory lock held for the tenure (`DutyLockKey`), row via `record_capability` | `maintainer` in phase 1 (opt-in). `migrator` in phase 4 (bootstrap closure, waiters) |
| Commit-order stamper leader | `PgCommitOrderStamperWorker`, `CommitOrderStamperLockKey` (#950) | Session advisory lock per schema, retried every `LeaderElectionRetry` | Phase 3, as a role per schema |
| Schema initialization lock | `SchemaBootstrapPhase`, generated `DbContextSchemaExtensionTemplate`, `SchemaMigrationDeferral`, `SchemaInitializationLockKey` | `pg_try_advisory_xact_lock` per attempt, with waiters probing `pg_locks` | No. Mutual exclusion per attempt, not a tenure |
| Canonical temporal rewrite | `CanonicalTemporalRewritePhase` | `pg_try_advisory_xact_lock` per attempt | No |
| Instance-alive lock | migration 055, `IInstanceAliveLockSource` | Session lock per instance: membership, not election | No (liveness input) |
| Collective apply serialization | `CollectiveApplyLockKey`, Dapper and EF Core appliers | `pg_advisory_xact_lock`, shared or exclusive | No. Serialization |
| Per-stream event-store locks, notify-state dedupe | migrations 029…162, 143/146 | xact locks | No |
| Per-window CAS claims | `EFCoreWorkCoordinator._tryClaimWatermarkAsync`: integrity audit, type-definition reconcile, offload sweep, row-cap sweep, settled fold; `AdvanceIntegrityCheckpointAsync` | `wh_settings` compare-and-swap, once per window | No. A claim per window is already the right shape |
| Backlog-age duty | `BacklogAgeWorker` | **Not elected.** Deliberately per-instance; its remarks mention `IDutyElector` only to say it is not scheduling | No |

## 2. Phase 1: schema, SQL, elector, liveness and release (this branch)

Delivered test-first, in slices:

1. **Migration `173_RoleAssignments.sql`**: `wh_role_assignments` plus `wh_elect_role`,
   `wh_renew_role`, `wh_release_role`, `wh_assert_role_epoch` and `wh_role_assignment_status`.
   Additive; not in the bootstrap closure, because nothing elects a role before migrations run in
   phase 1 (`migrator` stays on the session lock). Linted with `scripts/Lint-MigrationSql.ps1`.
   Tests: `RoleAssignmentSqlTests`.
2. **Contract**: `IDutyGrant.Epoch`, a default interface member returning `null`. Existing grants
   and every test fake keep compiling, and a grant that has a fencing token can hand it to
   exclusive-work SQL. Tests: `DutyGrantContractTests`.
3. **Options**: `RoleAssignmentOptions` (`RenewInterval`, `MissedRenewalsBeforeLapse`,
   `CooldownAfterLapse`, `HoldLegacySessionLock`, `Roles`), with `Lease` derived and validation that
   refuses a lease shorter than a renew interval and refuses `migrator`. Tests:
   `RoleAssignmentOptionsTests`.
4. **Elector**: `PgRoleElector : IDutyElector` in `Whizbang.Data.Postgres`. Roles it manages go
   through the vote, and everything else is delegated to the session-lock elector. The grant renews
   in `VerifyStillHeldAsync`, throttled on the monotonic clock, releases in `DisposeAsync`, and holds
   the legacy session lock while bridged. Tests: `RoleAssignmentElectorE2ETests`.
5. **Shutdown release**: `IReleasesDutiesOnShutdown` plus the `DutyShutdownReleaseService`
   hosted service, and `PgRoleElector.ReleaseAllAsync`. Tests: `DutyShutdownReleaseServiceTests`
   (Rocks) and an E2E test.
6. **Registration**: `AddWhizbangRoleAssignment()` replaces the `IDutyElector` registration and
   keeps `PgDutyElector` as the delegate. The default registration is unchanged. Tests:
   `RoleAssignmentRegistrationTests`.

Phase 1 test coverage, mapped to the issue's requirements: 1 (a stale epoch refused in SQL), 2 (a
crashed holder voids after the lapse bound, time advanced in the database), 3 (an unrenewed lease
lapses although the instance still heartbeats), 5 (cool-down), 6 (release hands off at once), 8 (the
assignment survives every connection being terminated), 9 and 10 (status), mixed-version bridge in
both directions, and ten instances racing, where exactly one holder and one epoch act.

**Determinism.** No test sleeps. Database time is advanced by shifting the stored timestamps back
(`_ageAsync`), which is the same thing as the database clock moving forward, since every comparison
is `stored < now()`. The grant's renew throttle is driven by `FakeTimeProvider`.

### 2.1 Phase 1 as delivered

- Migration 173 as planned, plus a test that pins the vote lock key's schema scoping (#962).
- `PgRoleElector` checks the bridge session on **every** verify, not only when renewing. Once the
  bridge session dies, an old instance can take the session lock, so "held" can no longer be
  answered from memory.
- The bridge is unlocked explicitly before its connection is disposed. Disposing a pooled
  connection returns the session to the pool with the advisory lock still held, and the pool
  resets the session only when it is next handed out. This was found by the mixed-fleet tests.
- A renewal that fails for a transient reason answers false without marking the grant lost, since
  the lease may still be valid, and the next verify asks again. An explicit refusal (a lapsed
  lease, a wrong epoch, a tombstone) marks it lost for good.
- The C# status reader (`ReadAssignmentsAsync`) moved to phase 2 with the health component. In
  phase 1, `wh_role_assignment_status()` is the observable surface.
- Requirement 8 is tested by terminating every backend of the database and clearing the pools,
  not by restarting the container. The phase 4 chaos suite restarts the server for real.

Test results: `RoleAssignmentSqlTests` 17, `RoleAssignmentElectorE2ETests` 22,
`RoleAssignmentRegistrationTests` 3, `RoleAssignmentOptionsTests` 7,
`DutyShutdownReleaseServiceTests` 4 (Rocks), `DutyGrantContractTests` 1, all passing. The full
`Whizbang.Core.Tests` suite passes (12,545). The existing duty, capability, rewrite,
schema-initialization, migrator-staging and migrations suites, and the Dapper schema-initializer
suite, pass unchanged. New lines in `PgRoleElector` and the registration are at 100% line and
branch coverage.

## 3. Phase 2: the acquisition hook and visibility

- A pending-work table keyed by `(role, step)`, written when the work becomes owed and cleared when
  it is confirmed done. The table-rewrite request record is the first producer.
- The acquisition hook: when `PgRoleElector` grants a role, whether at startup or later, the
  holder runs every owed step for that role under the grant's epoch. A background re-vote loop, with
  a cadence of one renew interval, is what makes "later" happen. Its liveness comes from the loop
  itself, which is also the loop that renews.
- `pg_notify('wh_role_released', role)` from `wh_release_role`, so waiters re-vote at once.
- `PgRoleElector.ReadAssignmentsAsync` over `wh_role_assignment_status()`, and a health component
  that reports "role unassigned" as degraded; metrics `whizbang.roles.elections`,
  `whizbang.roles.handoffs{reason}`, `whizbang.roles.lost`, and a gauge of held roles.

### 3.1 Phase 2 as delivered

- **Schema:** added to migration 173 in place rather than as a new file, because 173 has not been
  released and ships in the same PR. `wh_role_pending_work` is keyed by `(role, work_key)` and has
  `wh_owe_role_work`, `wh_owed_role_work` (with a `due` flag: the retry base doubled per failure,
  capped at one hour, in database time), and the fenced `wh_complete_role_work` and
  `wh_fail_role_work`. `wh_release_role` now sends `NOTIFY wh_role_released`, and
  `wh_role_assignment_status()` gained `pending_work`, dropped and recreated because its return
  type changed.
- **Pending until done:** completion presents the `last_owed_at` it listed and deletes only when
  nobody owed the work again since, so a need that arises mid-run is never lost. Completion and
  failure are fenced by `(holder, epoch)`, so a holder interrupted by a hand-off can neither mark
  the work done nor record against it.
- **Core:** `IPendingDutyWorkStore`, `PendingDutyWork`, `DutyWorkCompletion`, `IDutyWorkHandler`,
  `DutyWorkResult` (done / not done / deferred), `IRoleAssignmentReader`,
  `RoleAssignmentSnapshot`, `RoleAssignmentState`, `RoleAssignmentMetrics`,
  `RoleAssignmentHealthSource` (component `roles`), `DutyHolderWorker` (the acquisition hook) and
  `StartupStepDutyWork` (a duty-bound startup step as owed work). `RoleAssignmentOptions` gained
  `OwedWorkRetryBase`.
- **The rolling-deploy gap is closed.** `StartupPipelineRunner` takes an `IPendingDutyWorkStore`
  (a required parameter with a `NullPendingDutyWorkStore` default registered with the framework
  defaults and displaced by the Postgres store; the three-argument constructor is kept): a `Skip`
  step it skipped as a non-holder is owed to the duty. The holder
  loop wins the role when the old holder stops, and runs the step once.
- **One tenure per role per process.** `PgRoleElector` now hands out a handle on a shared tenure
  when this instance already holds the role, so the holder loop and a startup step can hold it at
  the same time; the last handle to close releases it. Without this, the step's dispose would have
  released the role under the loop.
- **A fence refusal drops the grant at once.** When completing or failing owed work is refused, the
  loop lets the grant go instead of trusting its throttled "still held" answer until the next
  renewal.
- **Status reader:** `PgRoleElector.ReadAssignmentsAsync`. **Metrics:** elections, hand-offs by
  reason, losses, releases, a held-roles up/down counter, and owed-work runs by outcome.
- **Registration:** `AddWhizbangRoleAssignment()` now also wires the store (which the pipeline
  runner picks up), the holder loop as a hosted service, the health source, the reader and the
  metrics.

Tests added: `RoleAssignmentSqlTests` +5 (22 in total), `RoleAssignmentElectorE2ETests` +4 (26),
`PendingDutyWorkE2ETests` 4, `DutyHolderWorkerTests` 18, `StartupStepDutyWorkTests` 7,
`RoleAssignmentHealthSourceTests` 7 (Rocks), `RoleAssignmentMetricsTests` 2,
`StartupPipelineRunnerOweTests` 3, and `RoleAssignmentOptionsTests` +1. The E2E tests include the
rolling-deploy gap end to end, and work interrupted by a hand-off completing exactly once with the
stale holder's write refused.

**Reversible defaults** chosen rather than designing around the open questions (#968):
first-valid-caller selection; no drain or hand-off to a newer version; the cool-down applies after
any lapse, including one caused by a database outage; while bridged, a stalled holder blocks
takeover until its session dies; work that outlasts a lease is allowed to lapse (it is fenced and
re-run by the next holder, so it must be idempotent). Each is one option or one SQL predicate to
change.

## 4. Phase 3: default and more roles

- `AddWhizbangRoleAssignment()` becomes the default in the notification stack, still bridged.
- The commit-order stamper leader becomes a role per schema. Its stamping loop is the renewer.
- Progress-tied renewal for long single statements (see open questions in the proposal).
- A multi-role vote, `wh_elect_roles(text[])`, if measurement shows per-role votes cost anything.

## 5. Phase 4: chaos and the end of the bridge

- The chaos suite: kill, `SIGSTOP`, partition, clock skew, database restart, a rolling deploy with
  old and new versions, and ten concurrent starts. Asserts at most one epoch writes, a bounded
  failover, and exactly-once pending work.
- `HoldLegacySessionLock` defaults to false, and the session-lock elector remains only for
  `migrator` until that duty joins the bootstrap closure and its waiters watch the assignment row.

## 6. Decisions

- **Candidates nominate themselves.** The vote assigns only the caller. Assigning an instance that
  is not asking would leave the role held by an instance that does not know it holds it.
- **No foreign key to the instance row.** A cascade would delete the epoch; the vote detects a reaped
  holder instead.
- **The fence checks holder and epoch together.** An asynchronous-replica failover can lose the last
  epoch increment, so the same epoch number can be issued twice. The holder id keeps the two apart.
- **Contended attempts are read-only.** A live holder is visible without the vote lock or a write, so
  pollers cost one `SELECT`. That is why the role elector does not need the session elector's
  in-memory contention backoff, which existed because every attempt opened a dedicated connection.
- **Liveness is the lease, plus registration and tombstone.** The vote does not read
  `last_heartbeat_at`, because its threshold depends on each writer's cadence (the alive-lock mode
  stretches it). Reaping, which is the fleet's verdict on the heartbeat, removes the row and
  tombstones it, and that voids the assignment.
- **Opt-in for phase 1.** The default registration is untouched, so no existing deployment changes
  behavior until it calls `AddWhizbangRoleAssignment()`.
- **The contract evolves by one default member.** `IDutyGrant.Epoch` is `long?` with a `null`
  default, so the fencing token can reach exclusive-work SQL without breaking any implementer.
  `TryAcquireAsync` keeps its shape, and the three refusals keep their meaning: `cooling_down` and
  `legacy_holder` are `Contended`, because waiting resolves both.
- **The fence reads `clock_timestamp()`, not `now()`.** A fenced transaction may have started long
  before it calls the fence. The vote, renewal and release are single statements, so `now()` is
  the call time there.
- **Hand-offs are logged by the winner only.** Exactly one vote produces each epoch, so each
  hand-off is logged exactly once, with the previous holder and the reason from the row.
- **A vote that voids a lapsed holder commits the void even when it refuses the caller**, for
  example during that caller's own cool-down. The vacancy is a fact, and the status surface
  should show it.
- **Phase 2 extends migration 173 in place.** It is unreleased and ships in the same PR, so one
  feature is one migration. The status function is dropped and recreated because its return type
  changed, which keeps a development database that ran the phase 1 text working.
- **Owed work for a duty not held by assignment is not recorded**, since nothing would run it.
- **A step that is disabled owes nothing**, and a step owed while this instance's own pipeline is
  still running is deferred (not failed), so the holder loop never blocks and its lease keeps
  moving.
- **The payload of the release NOTIFY is the role alone.** A same-named role in another schema of
  the same database causes a spurious wake, which costs one read.
- **No new optional injected parameters.** The repo's ratchet (`CompositionSatisfiabilityTests`)
  forbids them, so the runner's store is a required parameter with a null default, and the holder
  loop's notify connection is required and nullable, passed explicitly at every site.
- **The `roles` health component is Degraded, never Faulted**, both when a role is unassigned and
  when its read fails: restarting this instance would not give the role a holder.

## 7. Open questions (to be filed as `question` issues)

- Long single statements (`VACUUM FULL`, a large migration statement) can outlast a lease, and the
  loop cannot renew while it waits. Should the lease be sized for them, or should renewal be tied
  to the duty backend being observed `active` in `pg_stat_activity`?
- Should a newer-version instance be able to ask a live older holder to drain and hand over?
- Selection policy for a vacant role: first valid caller (today), newest library version, or
  longest-lived instance?
- While bridged, a stuck new holder keeps its session lock. Should a vote winner be allowed to
  terminate a lapsed bridge holder's session?
- An outage longer than the lease lapses every holder, which then pays the cool-down although the
  database was at fault. Should a lapse that coincides with a database outage be exempt?
