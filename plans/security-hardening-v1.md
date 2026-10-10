# Security hardening before 1.0

**Status:** in progress (started 2026-10-10).

The formal security review (`docs/security-review-2026.md`, draft PR #1155) is done once 1.0 fixes the
public surface. Until then its questions are a hunting guide: each one is answered against the code by
reading the path end to end and trying to break it. A defect is fixed test-first; a vulnerability gets a
private advisory, drafted with the `security-advisory` skill and published after a release carries the fix.

Record each item's outcome here (what was checked, what was found, the PR or issue) as it is worked.

## Done

- **The request scope's identity comes from the authenticated principal only.** The scope middleware no
  longer reads tenant, user, organization or customer ids from request headers unless the application names a
  header (#1316, docs whizbang-lib.github.io#1077).

## Hunt list (highest risk first)

| # | Question | Where to look | Outcome |
|---|---|---|---|
| 1 | Does every HTTP endpoint the framework generates (GraphQL lenses, REST lenses, SignalR) read through the caller's scope, with no path that bypasses it? | HotChocolate and FastEndpoints generators, `Whizbang.SignalR`, scoped lens factory | **Done 2026-10-10.** GraphQL and REST lenses read through the lens's default scope (`QueryScope.Tenant`), and a missing tenant throws rather than reading every tenant: fails closed. SignalR notifications did not: a tag with no group broadcast to every client, an unresolved placeholder was sent to a literal group, and a payload field could choose `{TenantId}`. Fixed in #1316 (docs whizbang-lib.github.io#1077). Group membership stays the application hub's, now documented. |
| 2 | Can a caller read a field its permissions hide, or run a query with no bound (depth, cost, page size)? | `FieldPermissionAttribute`, lens paging attributes, GraphQL production-hardening docs | |
| 3 | Does any log line or exception message include a message payload, a credential or personal data? | `LoggerMessage` definitions and exception messages across `src/` | |
| 4 | Are identifiers that come from configuration (schema, table names) safe where they reach SQL, and is configuration documented as trusted? | `Whizbang.Data.Postgres`, `CollectiveApplyHookPlan`, schema naming | |
| 5 | Can the application run with a database role that has no DDL rights, and is the migrator / runtime split documented? | connection roles, `ai-docs/schema-initialization-connections.md` | |
| 6 | Can a type name received in a message make the process load an assembly or create an unregistered type? | `MultiPassMessageTypeBinder`, the generated JSON-context by-name fallback, `AuditOutboxMessageBuilder` | |
| 7 | Are offloaded message bodies protected at rest by default, and do the docs say what the default is? | `Whizbang.Core/Offloads`, offload docs | |
| 8 | Can unreviewed code reach a release, and is every long-lived automation credential scoped and expiring? | release-branch ruleset, `PUBLISH_WITHOUT_APPROVAL`, `RELEASE_PAT`, `DOCS_REPO_PUSH_TOKEN` | |
| 9 | Does the broker stay inside the trust boundary: do the transports require credentials and TLS by default, and does nothing expose the broker to callers? | `Whizbang.Transports.AzureServiceBus`, `Whizbang.Transports.RabbitMQ` options and docs | |
