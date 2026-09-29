# Duty role assignment: the advisory lock decides the vote, the row is the role

Status: phase 1 in progress on `feat/966-role-assignment`. Issue #966. Design and requirement mapping:
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

## 3. Phase 2: the acquisition hook and visibility

- A pending-work table keyed by `(role, step)`, written when the work becomes owed and cleared when
  it is confirmed done. The table-rewrite request record is the first producer.
- The acquisition hook: when `PgRoleElector` grants a role, whether at startup or later, the
  holder runs every owed step for that role under the grant's epoch. A background re-vote loop, with
  a cadence of one renew interval, is what makes "later" happen. Its liveness comes from the loop
  itself, which is also the loop that renews.
- `pg_notify('wh_role_released', role)` from `wh_release_role`, so waiters re-vote at once.
- A health component that reports "role unassigned" as degraded; metrics `whizbang.roles.elections`,
  `whizbang.roles.handoffs{reason}`, `whizbang.roles.lost`, and a gauge of held roles.

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
