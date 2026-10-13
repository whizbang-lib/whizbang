# Multi-stream commands (#1358)

Status: in progress (started 2026-10-13). Board #1, In progress. First consumer: #1357 (rebuild stream work follows
stream ownership), done in its own PR once this lands.

## What it is

A command that names a set of streams. Whichever instance receives it records a **run**: one row per stream. Each
instance then claims only the run's streams **it owns**, by the rule claiming already uses, and invokes the
application's per-stream handler once per stream. Each stream's outcome is recorded; the run completes when every
stream has one, and a completion event is published.

```csharp
public sealed record ReindexOrders(Guid RunId, IReadOnlyList<Guid> StreamIds) : IMultiStreamCommand;

public sealed class ReindexOrdersHandler : IMultiStreamCommandHandler<ReindexOrders> {
  public ValueTask HandleStreamAsync(ReindexOrders command, Guid streamId, CancellationToken cancellationToken) { ... }
}

services.AddMultiStreamCommandHandler<ReindexOrders, ReindexOrdersHandler>();
```

## Decisions

- **Delivery through the database, not the transport.** The command travels as any command does (one instance of the
  service receives it). Its intake receptor records the run; every instance reaches the run through the database
  (NOTIFY `wh_multi_stream_work`, poll as backstop). Transport-agnostic.
- **Run id is on the command** (`IMultiStreamCommand.RunId`), chosen by the caller. Recording is idempotent on it, so
  a redelivered command, or one recorded both before the outbox and after the inbox, makes one run. The caller can
  read the run's status by it.
- **Empty stream list is refused** at intake (`ArgumentException`); never widened to "every stream" (#1356).
  Duplicate ids in the list collapse to one stream.
- **Ownership rule** (in SQL, one function): a stream whose `wh_active_streams` owner is this instance is mine; a
  stream owned by another live instance (lease unexpired, and a fresh heartbeat or a visible connection) is not;
  otherwise the stream's partition decides: `partition_number % count = rank`, rank and count from the published
  partition assignment when the caller's cached version is current, else the same live-instance ranking `claim_work`
  falls back to. The partition hash stays in Postgres.
- **No write to `wh_active_streams`.** Pinning an unowned stream would add a writer to the work-table lock order. It is
  not needed: an unowned stream's live work goes to its partition owner, which is the same instance by the same rule.
- **Work item lease.** A claimed stream carries this instance and a lease expiry; an instance that dies mid-run lets
  the lease lapse and the stream is re-offered to its (new) owner. A completion is recorded only by the instance that
  holds the item.
- **Retries.** A handler that throws is retried up to `MaxAttempts` (default 3), then the stream is recorded failed
  with the error's type and message. Shutdown cancellation records nothing (the lease lapses).
- **Security scope.** The intake captures the ambient scope as a `ScopeDelta` and the worker restores it around each
  handler call, so a handler runs under the caller's tenant and principal as a receptor would.
- **AOT.** Registration is generic over the command type at compile time (`MultiStreamCommandBinding<TCommand>`); the
  payload is serialized and read with the `JsonTypeInfo` the registry already holds. No reflection.
- **Retention.** Completed runs are purged after `CompletedRunRetention` (default 7 days).

## Slices

1. Core contracts, binding, intake receptor (unit, fake store).
2. Migration 208: tables and functions (record, claim, complete, read, purge) + Postgres store (integration, real PG).
3. Worker: claim, invoke, retry, complete, publish completion (component, fake store).
4. Driver registration + NOTIFY subscription; two-instance end-to-end (integration).
5. Docs page `fundamentals/dispatcher/multi-stream-commands`, work-coordination page corrected (it predates the
   published assignment), maps regenerated from this worktree.
