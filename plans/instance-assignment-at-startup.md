# Plan: every instance has an assignment, and roles follow the live roster

Status: DESIGN, for owner review. Issue: #1328. Owner decision already recorded on the issue: assignment is by **vote
with a quorum**; with no quorum (for example a single live instance) the one instance takes every role rather than
losing and re-winning it; the shared record of who holds what stays centralized in the database.
Evidence: survey of develop @ 31304b25d (after PR #2570). SQL paths are under `src/Whizbang.Data.Postgres/Migrations/`.

## 1. Problem

#1327: a single-instance service lost the `commit-stamper` role to its own lease four times in two minutes while
draining a backlog. Each lapse cost a cool-down, then the same instance won the role back. The vote could not tell
"the holder is stuck and a peer should take over" from "the holder is busy and it is the only member".

The issue asks for four things:

1. **Startup:** each instance registers its membership and receives an assignment before it starts work. Roles go to
   members; everyone else is a standard worker.
2. **A lapse with no other live member:** the holder is reaffirmed, with no cool-down and no vote.
3. **Handover:** an explicit reassignment when a member leaves, lapses or drains. Mixed-version drain is a property of
   the roster.
4. **Startup storm control:** only assigned members run the expensive boot work.

## 2. What exists today

Most of #1328 has been delivered since it was filed, by #966/#968 (role assignment), #2027/#2044 (one liveness rule,
every duty is a role), #2140 (holders judged by the liveness rule), #1636/#1638 (startup stages, ready before
assigned) and #2540 to #2559 (partition assignment as the only owner of a stream, PR #2570).

| #1328 ask | State | Where |
|---|---|---|
| Register membership at startup (id, version, service, liveness) | **Done.** The instance registers at the end of Preflight, before Elect, and records its stage on its own row. `library_version`, `service_name`, `connection_mode`, `startup_stage` are columns. | `231_StartupStageRecord.sql`, `277_PreflightInstanceAndRoleColumns.sql`, `316_OneDurableInstanceRecord.sql` (`wh_instance_liveness`, `wh_instance_is_live`, `wh_live_instance_ids`) |
| One liveness rule | **Done.** Every reader asks `wh_instance_is_live` (a fresh heartbeat or a held alive-lock). | `316`, `CoordinationSourceGuardTests` |
| A standard-worker share before work starts | **Done in substance, not in name.** The partition assigner publishes a map over the instances eligible for work; an instance is eligible only from Accept commands (60). In map mode every claim goes through `wh_i_own`, so an instance with no partitions claims nothing. Startup steps that ask for nothing narrower require `StartupCapabilities.EVERY_INSTANCE`. | `PartitionAssigner.cs`, `PartitionAssignerWorker.cs`, `565_PartitionsAreOwnedByThePublishedMap.sql`, `wh_instance_is_eligible_for_work` (231), `StartupStepDescriptor.cs` |
| Minimal moves, settle window, handover, epoch fencing | **Done.** | `PartitionAssigner.cs` (`SettleWindow`), `PartitionHandoverWorker.cs`, `570`/`571`/`572`/`577`/`580`/`581`, `plans/archive/stream-ownership-follows-assignment.md` |
| Roles held by one mechanism, with lease and epoch | **Done.** `migrator`, `maintainer`, `commit-stamper`, `partition-assigner` and `perspective-rebuild:*` are rows in `wh_role_assignments`, won by `wh_vote_role`/`wh_vote_roles`, fenced by `wh_assert_role_epoch`. | `173`, `184`, `326_EveryDutyIsARoleAssignment.sql`, `355_RoleHoldersAreJudgedByTheLivenessRule.sql`, `PgRoleElector.cs`, `DutyHolderWorker.cs`, `RoleAssignmentOptions.cs` |
| Work in progress counts as liveness | **Done.** A marked duty backend running a statement holds the role (`MarkDutyBackendAsync`, 184); past its lease a holder keeps the role while the liveness rule calls it live and it renewed inside its progress window (355, default 2 minutes). This removed the common #1327 case. | `355`, `RoleAssignmentOptions.ProgressWindow` |
| Handover on leave, drain, newer version | **Done.** Graceful release on shutdown (`DutyShutdownReleaseService`, `NOTIFY` on release), cooperative drain toward the newest library version (`wh_role_candidates`, `drain_requested_at`), older instances stand down (#2022), partitions handed over before deregistering (`PartitionHandoverWorker` as a held-work releaser). | `184`, `326`, `PartitionHandoverWorker.cs` |
| Shared record in the database | **Done.** No transport-side roster exists, and the owner's decision keeps it that way. | |
| **No quorum: the sole member is reaffirmed, no cool-down** | **Not done.** A sole holder that is stuck past its progress window is voided as `lapsed`, and the vote then answers `cooling_down` to that same instance for `CooldownAfterLapse` (15 s) although nobody else can take the role. `_role_lapse_reason` and the cool-down branch of `wh_vote_role` never ask whether a peer exists. | `326` (`wh_vote_role`), `355` (`_role_lapse_reason`) |
| **An instance's assignment as one readable fact** | **Not done.** The facts exist in three places (`wh_service_instances`, `wh_role_assignments`, `wh_partition_ownership`); nothing joins them, nothing names "standard worker", and the instance never logs what it was given. `FleetInstanceStatus` reports held capabilities but no partition share; the Dapper driver has no fleet status source at all. | `StartupFleetStatus.cs`, `EFCorePostgresStartupFleetStatusSource.cs` |
| **The set of roles is declared in one place** | **Partly.** `RoleAssignmentOptions.Roles` lists the managed roles, and drivers add theirs by post-configure (`PartitionAssignerRegistration`, `RoleAssignmentServiceCollectionExtensions`), but a worker can call `TryAcquireAsync` with any string, and nothing fails when a held role is missing from the list (the health component and the roster would not show it). | `PartitionAssignerWorker.cs:135`, `PgCommitOrderStamperWorker.cs:321` |
| **Storm control: only assigned members run expensive boot work** | **Not done.** The migrator is one instance, and the periodic audits are per-window CAS claims, but the Converge stage runs on every instance at every start: analyze of newly indexed tables, the managed-object reconcile (catalog reads), the perspective registry currency check, and table settings. | `DbContextSchemaExtensionTemplate.cs` (after `ConvergingAsync`), `DapperSchemaInitializationRunner.cs` |

So the remainder is four pieces: the quorum rule, the assignment as a readable fact, one declaration of roles, and the
storm-control home.

## 3. Design

### 3.1 Principles

- **D1. The vote stays; the roster is who can be asked.** Candidates still nominate themselves (duty-role-assignment
  decision: an instance assigned a role it did not ask for does not know it holds it). The roster is the set of live
  candidates for a role: rows in `wh_role_candidates` whose instance the one liveness rule calls live and that are
  registered and not tombstoned.
- **D2. A lapse needs a successor.** "The holder lapsed" is a verdict that only matters when someone else could hold the
  role. Without a quorum the verdict is not given, so the holder keeps its assignment and its epoch.
- **D3. One source per fact.** An instance's assignment is derived from the tables that decide it, never stored a second
  time. Roles come from `wh_role_assignments`, the share from `wh_partition_ownership`, the stage and liveness from
  `wh_service_instances` and the liveness rule.
- **D4. Gravity.** Declaring a role is one registration, and the only way to get the holder loop, health, roster and
  quorum for it. A guard test fails when code votes for a role that was not declared.
- **D5. The rule lives in SQL.** Quorum and lapse are decided by the shared functions every release calls, so a
  mixed-version fleet never runs two rules for one role.

### 3.2 Quorum (the sole-member rule)

A new helper, `_role_has_successor(p_role, p_holder) -> boolean`: whether a live candidate for `p_role` other than
`p_holder` exists. "Live" is `wh_instance_is_live` (316), plus registered and not tombstoned, plus a candidacy no older
than one lease (`_role_candidate_live`), so a peer that stopped voting for the role does not count.

Applied in two places, both redefined in one new migration whose role functions sit in a bootstrap region (the migrator
is won by this vote before migrations run):

1. **`_role_lapse_reason`:** the `lapsed` branch also requires `_role_has_successor`. `evicted` and `unregistered`
   are unchanged: a tombstoned or reaped holder is gone whether or not a peer exists. A live sole holder therefore keeps
   its role past its lease and past its progress window, at the same epoch, and its next renewal or vote answers `held`.
   The fence (`wh_assert_role_epoch`) calls the same function, so it keeps accepting that holder's writes: there is no
   other writer.
2. **The cool-down branch of `wh_vote_role`:** `cooling_down` is answered only when a live candidate that is not
   cooling down exists. A holder voided while a peer existed, whose peer then left, wins the role straight back.

The moment a peer votes, its candidacy is written in the same statement, before the fast path reads the assignment, so
that vote sees a successor, voids a stuck holder and wins as today. Takeover of a stuck holder is therefore unchanged
in a fleet of two or more; it only stops happening when there is nobody to take over.

A holder the liveness rule calls dead still lapses at its lease (a restarted process registers a new instance id, so it
is not the cooling-down holder). A live holder stalled past its progress window with no successor is reported, not
voided: the `roles` health component reports it Degraded ("stalled, no peer to take over"), and a metric
`whizbang.roles.stalled_without_successor` counts it. Restarting the instance is the operator's or the orchestrator's
call, as it is for any stuck process.

### 3.3 The assignment as a readable fact

- **SQL:** `wh_instance_assignments()`, one row per registered instance: instance id, service, host, library version,
  stage, eligible, live, the roles it holds (with epochs), the number of partitions it owns, the published map version,
  and a derived `assignment` kind:
  - `starting`: registered, not yet eligible for work;
  - `standard-worker`: eligible, owns partitions, holds no long-tenure role;
  - `role-holder`: eligible and holds at least one role (it is a standard worker as well);
  - `awaiting-share`: eligible, live, but owns no partition in a map published after it became eligible (a symptom
    worth seeing, for example when the assigner is unheld);
  - `draining`, `ineligible` (an older release after the map switch, #2555), `evicted`.
  It reads only the existing tables (D3). The per-instance part is a primary-key probe set; the share is one grouped
  count over the 10,000-row ownership table.
- **C#:** `IInstanceAssignmentReader` with `InstanceAssignment` records, implemented once in `Whizbang.Data.Postgres`
  over the function so the EF Core and Dapper drivers share it. `FleetInstanceStatus` gains `Assignment` and
  `OwnedPartitions`, and the Dapper driver gains the fleet status source it lacks today (parity, see
  `plans/dapper-efcore-startup-parity.md`).
- **The instance says what it was given.** Each instance logs its assignment when it first appears in a published map
  and on every change ("Assigned: standard worker, 2,500 of 10,000 partitions, map 12" or "Assigned: commit-stamper,
  partition-assigner and 2,500 partitions"), from the events that already exist (`OnPublished` reaches only the
  assigner today, so the claimer side reads the map version from `PartitionAssignmentCache` and the roles from its own
  grants). An `assignment` health detail carries the same.
- **No new startup gate.** Readiness (Running) does not wait for the first share (owner decision 3). The `awaiting-share`
  kind and a Degraded health detail after `AwaitingShareGrace` (default two settle windows plus one renew interval,
  bound from configuration) make a missing share visible instead.

### 3.4 One declaration of roles

- `RoleCatalog` (a registration, not a helper): `services.AddWhizbangRole(name, kind)` where kind is `Standing` (held
  by a loop for a tenure: `maintainer`, `commit-stamper`, `partition-assigner`) or `Episodic` (held for one unit of
  work: `migrator`, `perspective-rebuild:*` as a prefix). It replaces the post-configure additions to
  `RoleAssignmentOptions.Roles` and the `IsEpisodic` special case, and it is what the holder loop, the health
  component, the roster function's role list and the quorum candidate set read.
- The roster shows a declared standing role with no holder as `unassigned`, which the health component already reports
  as Degraded for the roles it knows.
- **Guard test**: every `TryAcquireAsync`/`TryAcquireManyAsync` call site in `src` passes a constant or prefix that a
  registration declares (scanned like `CoordinationSourceGuardTests`), and every declared standing role has exactly one
  loop that holds it. A new role that skips the registration fails the build.

### 3.5 Storm control: the boot work gets an owner

- Every unit of work done at every start is declared as a startup operation with a scope: `PerInstance` (it changes
  only this process, for example warming a cache) or `PerSchemaPerBuild` (it changes shared state the same way from any
  instance: the analyze, the managed-object reconcile, the perspective registry currency check, the table settings).
- `PerSchemaPerBuild` work runs once per schema per build hash: the first instance claims it with the existing
  per-window CAS pattern (`_tryClaimWatermarkAsync` style, keyed by build hash), the rest skip it and log that a peer
  did it. A failed run releases the claim so the next start retries. When the migrator ran a pass in this start, it
  holds the claim already.
- Guard test: the Converge stage runs only registered operations, and every registered operation states its scope.
- A fleet-wide cap on concurrent heavy starts (startup permits) is the next step of storm control and is not in this
  plan (owner decision 4).

## 4. Alternatives considered

| Alternative | Why it lost |
|---|---|
| A roster leader assigns roles (extend the partition assigner to hand out roles) | Contradicts the recorded decision (vote with a quorum). An assigned instance that is not asking does not know it holds the role. The assigner is itself a role, and the migrator must be chosen before migrations run, before any assigner can exist, so the vote is needed anyway: two mechanisms instead of one. |
| Roles computed deterministically from membership (lowest id, rendezvous hashing) | Every instance computes its own answer; during a membership change two instances disagree, which is the exact failure stickiness and then the published map were built to remove (rank-churn wedge, PR #194). It also loses the newest-version preference and cooperative drain. |
| A stored assignment table written at startup | A second record of facts already decided by `wh_role_assignments` and `wh_partition_ownership`; it drifts the moment either moves, and every write path would have to remember to update it (the opposite of gravity). |
| Only skip the cool-down when there is no peer (keep voiding the lapsed holder) | Still loses and re-wins the role: epoch churn, lost in-flight work, `OnStoppedLeading` and a re-run of owed work for nothing. The owner chose reaffirmation. It is the cheaper half, and it is kept for the "peer left during the cool-down" case (3.2, item 2). |
| Majority quorum over the registered roster | Quorum in consensus systems prevents split brain across partitions. Here the database linearizes every vote, so split brain cannot happen, and a majority rule would only make a degraded fleet slower to recover (owner decision 1). |
| A transport-reachable roster for when the database is degraded | The owner decided the record stays in the database. Every role's work needs the database anyway, so a role won over the transport could do nothing. A central service is a separate question (#1474). |
| Drop the lease for a sole member | Liveness is still needed: a sole member that dies must lapse so its restart (a new instance id) can win at once. 3.2 keeps the lease and removes only the verdict that needs a successor. |
| Gate Running on the first share | Couples readiness (serving reads) to the assigner's cadence; an unheld assigner would hold every new pod unready, which is a restart storm in an orchestrator. Visibility through `awaiting-share` gives the signal without the coupling. |

## 5. Migration and upgrade impact

- **SQL:** one new migration for 3.2 (number: next free at implementation; current develop tip is 581). The redefined
  `_role_lapse_reason`, `wh_vote_role` and the new `_role_has_successor` go in a bootstrap region, as 355 did. One new
  migration for 3.3 (`wh_instance_assignments`, additive, outside the bootstrap region). One for 3.5 if the claim needs
  a settings key shape beyond what `wh_settings` CAS already offers. Each has a `.down.sql`.
- **Mixed versions:** every release from 184 on calls the same SQL functions, so the quorum rule applies to old and new
  instances at once (D5). The rule only extends a holding when no successor exists; it never shortens one, so an older
  instance cannot be surprised by losing a role sooner. An older release does not read `wh_instance_assignments`.
- **Behavior change:** a sole live instance stuck past its progress window keeps its roles instead of cycling them.
  Takeover in a fleet of two or more is unchanged. This goes under Changed in the changelog with an upgrade note naming
  the new health detail.
- **Public API:** additive (`IInstanceAssignmentReader`, `InstanceAssignment`, `FleetInstanceStatus` members,
  `AddWhizbangRole`). `RoleAssignmentOptions.Roles` stays readable; writes to it are routed through the catalog with an
  `[Obsolete]` pointer for one release.
- **Docs:** `operations/startup/capabilities-and-duties` (the quorum rule, the role catalog),
  `proposals/duty-role-assignment` (decision recorded), `operations/startup/startup-status` (the assignment kinds),
  `fundamentals/work-coordinator/partition-assignment` (the instance's share as part of its assignment),
  `operations/startup/startup-pipeline` (operation scopes), and a design-choice entry per principle linked to its
  locking test.

## 6. Phases (each a separate issue)

| # | Issue scope | Size | Depends on |
|---|---|---|---|
| 1 | **Sole-member rule**: `_role_has_successor`, `_role_lapse_reason` and the cool-down branch (3.2), the stalled-without-successor health detail and metric. Closes the #1327 class for good. | S (2 to 3 days) | none |
| 2 | **Role catalog**: `AddWhizbangRole`, standing vs episodic, the holder loop, health and options read it, guard test over acquire call sites (3.4). | S (2 days) | none |
| 3 | **Assignment as a fact**: `wh_instance_assignments`, `IInstanceAssignmentReader` in `Whizbang.Data.Postgres`, `FleetInstanceStatus` members, the Dapper fleet status source, the assignment log line and health detail, `AwaitingShareGrace` (3.3). | M (4 to 5 days) | 2 (for the role list) |
| 4 | **Boot work with an owner**: inventory and measure every-start work, scope on each operation, once-per-build claim for `PerSchemaPerBuild`, guard test (3.5). | M (4 to 5 days) | none |
| 5 | **Docs**: the pages in section 5, linked both ways to the locking tests. | S (1 to 2 days) | 1 to 4 |

Phases 1 and 2 are independent and can ship in one bundle. #1328 closes when 1 to 3 and 5 ship; phase 4 is linked
from it, or split out per owner decision 4.

## 7. Test strategy

Strict red/green, deterministic by construction: database time advanced by shifting stored timestamps back, never by
sleeping; the renew throttle on `FakeTimeProvider`; waits on the exact transition's signal.

| Choice | Test that fails if it flips |
|---|---|
| D2 no successor, no lapse | SQL: a live sole holder aged past its lease and its progress window votes `held` at the same epoch; the fence accepts its write |
| D2 a successor restores takeover | SQL: the same holder, then a live peer votes, and the peer is `granted` at epoch + 1 with `previous_holder` set; the old holder's fenced write is refused |
| Successor must be live | SQL: a peer with a candidacy row but a dead instance (no heartbeat, no alive-lock) does not count; a tombstoned peer does not count; a peer whose candidacy is older than one lease does not count |
| Dead holder still lapses | SQL: a sole holder the liveness rule calls dead lapses at its lease; a new instance id wins with no cool-down |
| Cool-down only with a peer | SQL: voided holder whose peer left wins straight back; with a live non-cooling peer, `cooling_down` as today |
| #1327 end to end | E2E: one instance, the stamper stalled past its progress window, no epoch change and no `OnStoppedLeading`; a second instance joins and takes over within one renew interval |
| Mixed versions | E2E: an instance calling the 184 entry points (`wh_elect_role`, `wh_renew_role`) gets the same answers |
| D3 derived assignment | SQL: each `assignment` kind from a seeded roster; moving a partition or a role changes the row with no other write |
| Driver parity | The same reader contract test run against EF Core and Dapper registrations |
| D4 gravity | Guard: an acquire call with an undeclared role fails; every declared standing role has one holder loop |
| 3.5 once per build | E2E: ten concurrent starts of one build run each `PerSchemaPerBuild` operation once; a failed run is retried by the next start; a new build hash runs it again |
| Health | Component: stalled-without-successor and awaiting-share are Degraded, never Faulted |

100% line and branch coverage of new code in each phase; hot-path note: `_role_lapse_reason` is called by every vote
and fence, so `_role_has_successor` is evaluated only inside the `lapsed` branch (after the lease and progress checks
fail), and its plan is checked with `EXPLAIN ANALYZE` against a candidates table of fleet size.

## 8. Decisions for the owner

1. **What counts as a quorum.**
   (a) At least one other live candidate for that role. (b) A majority of the registered roster. (c) A configured
   minimum replica count.
   **Recommendation: (a).** The database linearizes the vote, so a quorum only has to answer "is there a successor".
   (b) and (c) add configuration and slow recovery without adding safety.
2. **A sole holder stalled past its progress window.**
   (a) Keep the role and epoch, report Degraded (3.2). (b) Void it and let it re-win at once, so the role's loss token
   cancels the stuck loop in-process (a watchdog effect).
   **Recommendation: (a)**, which is what the recorded decision says. (b) recovers some stuck loops without a restart,
   but at the cost of the churn #1327 complained about, and a stuck process is better surfaced than masked.
3. **Should Running wait for the instance's first share?**
   (a) No gate; `awaiting-share` and a Degraded detail make it visible. (b) Running waits for the first published map
   that includes the instance, with a timeout.
   **Recommendation: (a)** (section 4, last row).
4. **Where storm control lives.**
   (a) Phase 4 of this plan, under #1328. (b) Its own issue, with the fleet-wide startup permit, linked from #1328.
   **Recommendation: (b).** The roster gives the boot work an owner, but measuring what is expensive and capping
   concurrent starts is its own workstream; keeping it separate lets #1328 close on phases 1 to 3.

## 9. Progress

Owner decisions recorded on #1328 (2026-11-25): quorum is at least one other live candidate; a stalled sole holder
keeps the role and is reported Degraded (a healthy sole holder is Healthy); no Running gate; storm control split to
#2596.

- **Phase 1, #2593 (done):** migration `615_ALapseNeedsASuccessor.sql`. `_role_has_successor` (a candidacy within the
  role's lease, not tombstoned, live by the one rule); `_role_lapse_reason` asks it last, inside the lapsed branch; the
  fence (`wh_assert_role_epoch`) accepts a live sole holder; `wh_vote_role` answers `cooling_down` only while a
  successor exists; `wh_role_assignment_status()` gains `stalled_without_successor`. `RoleAssignmentSnapshot`
  carries it, the `roles` health component reports it Degraded, and `whizbang.roles.stalled_without_successor` counts
  each read that sees it. Locked by `RoleLapseNeedsASuccessorSqlTests`, `RoleAssignmentHealthSourceTests` and
  `RoleAssignmentElectorE2ETests`.
