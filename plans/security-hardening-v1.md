# Security hardening before 1.0

**Status:** in progress (started 2026-10-10).

The formal security review (`docs/security-review-2026.md`, draft PR #1155) is done once 1.0 fixes the
public surface. Until then its questions are a hunting guide: each one is answered against the code by
reading the path end to end and trying to break it. A defect is fixed test-first; a vulnerability gets a
private advisory, drafted with the `security-advisory` skill and published after a release carries the fix.

Record each item's outcome here (what was checked, what was found, the PR or issue) as it is worked.

## Done

- **Items 2 to 9 of the hunt list** (outcomes in the table): query depth limit, REST page overflow, no payloads
  in logs or exceptions, shared identifier quoting, schema work on the `-init` role, a bounded type-name cache,
  sealed-body enforcement and a bounded blob read, and a manual publish limited to reviewed commits (#1316).
- **The request scope's identity comes from the authenticated principal only.** The scope middleware no
  longer reads tenant, user, organization or customer ids from request headers unless the application names a
  header (#1316, docs whizbang-lib.github.io#1077).

## Decisions for the owner

Approved by the owner on 2026-10-10, all as suggested. State:

1. **Publish environments limited to develop, main and `release/**`.** `release.yml` now starts from the push to main
   (a pull_request run's ref is `refs/pull/N/merge`, which a branch policy cannot admit safely); the policies on
   `nuget-publish`, `nuget-publish-auto` and `release-approval` are applied once that change is on develop.
2. **Tag ruleset "Protect release tags"** (id 24862485) is live: no actor may move or delete a `v*` tag. Restricting
   creation to the release workflow waits on item 5: GitHub does not accept GitHub Actions as a bypass actor, so
   the release jobs must create tags with an app token that the ruleset can name.
3. **`release/**` requires a pull request and the CI gate** (live; checks not enforced on creation, so Start Release
   and a hotfix cut still push the new branch). F7 hotfixes now arrive as a PR into the hotfix branch, like F8.
4. **SHA pinning enforced; `GITHUB_TOKEN` can no longer approve pull requests** (live).
5. **Replace `RELEASE_PAT` and `DOCS_REPO_PUSH_TOKEN` with a GitHub App token.** App `whizbang-release-bot`
   created 2026-10-11 (contents, pull requests, workflows and actions write; installed on this repository, the docs
   repository and the extension repository; org secrets `RELEASE_APP_ID`/`RELEASE_APP_PRIVATE_KEY` scoped to this
   repository and the extension). This repository's workflows mint per-job tokens instead of the PATs, tag pushes
   included (#1322). Ruleset "Restrict release tag creation" (24882819) lets only the app and repository admins
   create a `v*` tag. Verified: the first develop run after the merge published test status to the docs site as the
   app and its docs release succeeded. **Done 2026-10-11.** The extension repository made the same switch
   (whizbang-vscode#89) with its own two tag rulesets, and its publish now starts from the push to main behind a
   reviewed environment holding the Marketplace token (whizbang-vscode#91). Both PATs are revoked. Release v0.2619.0
   cut, tagged and published on the app; it needed no merge-back.
6. **Offload encryption stays opt-in; a store with no cipher logs a warning at start** (`UnsealedOffloadStartupWarning`).

## Hunt list (highest risk first)

| # | Question | Where to look | Outcome |
|---|---|---|---|
| 1 | Does every HTTP endpoint the framework generates (GraphQL lenses, REST lenses, SignalR) read through the caller's scope, with no path that bypasses it? | HotChocolate and FastEndpoints generators, `Whizbang.SignalR`, scoped lens factory | **Done 2026-10-10.** GraphQL and REST lenses read through the lens's default scope (`QueryScope.Tenant`), and a missing tenant throws rather than reading every tenant: fails closed. SignalR notifications did not: a tag with no group broadcast to every client, an unresolved placeholder was sent to a literal group, and a payload field could choose `{TenantId}`. Fixed in #1316 (docs whizbang-lib.github.io#1077). Group membership stays the application hub's, now documented. |
| 2 | Can a caller read a field its permissions hide, or run a query with no bound (depth, cost, page size)? | `FieldPermissionAttribute`, lens paging attributes, GraphQL production-hardening docs | **Fields: done 2026-10-10.** Nothing read `[FieldPermission]`, so protected fields went to every caller. Now enforced in GraphQL (type interceptor from `AddWhizbangLenses`) and REST (generated registration plus a JSON modifier; a generated endpoint refuses to respond when the masking is missing); protected fields cannot be filtered or sorted; no scope means not permitted. `[RequirePermission]` read the scope from a service only the message-security registration provides; it now reads `IScopeContextAccessor.Current`. Unused `[Scoped]` removed. In #1316 (docs whizbang-lib.github.io#1077). **Bounds: done 2026-10-10.** HotChocolate already enforces cost limits and a 30 second execution timeout; nothing bounded depth, so `AddWhizbangGraphQLSecurityDefaults` now adds a depth limit (15 by default, settable) in every environment, introspection not counted. REST page size was already capped; a page past the integer range wrapped the offset negative (a database error, or the first page on other providers) and now clamps. In #1316. |
| 3 | Does any log line or exception message include a message payload, a credential or personal data? | `LoggerMessage` definitions and exception messages across `src/` | **Done 2026-10-10.** Two unused debug log definitions in the Dapper coordinator would have written whole message bodies; removed. The lenient date converter echoed the caller's unparseable text into its exception, which reaches logs and error responses; it now reports the length. Connection-string logging names keys, never values. In #1316. |
| 4 | Are identifiers that come from configuration (schema, table names) safe where they reach SQL, and is configuration documented as trusted? | `Whizbang.Data.Postgres`, `CollectiveApplyHookPlan`, schema naming | **Done 2026-10-10.** Schema and table names come from the model and configuration, which are trusted (the operator writes them). The collective apply quoted them by hand; it now uses the shared identifier quoting. The table-redirect helper in Core rewrites only names the rebuilder generates. In #1316. |
| 5 | Can the application run with a database role that has no DDL rights, and is the migrator / runtime split documented? | connection roles, `ai-docs/schema-initialization-connections.md` | **Done 2026-10-10.** Startup migrations already used the `-init` connection when configured. Three paths after startup did not and needed schema rights on the application role: the managed-object reconcile, the blue-green rebuild's table swap, and the index-statistics `ANALYZE` (needs table ownership). The `-init` connection is now registered per context under the name startup resolves, and all three use it. In #1316. Docs: a least-privilege role setup on the migrations page (docs whizbang-lib.github.io#1077). Still open: the opt-in table rewrite runs `VACUUM FULL` on the application connection, and no test starts a service under a data-only role end to end; both before 1.0. |
| 6 | Can a type name received in a message make the process load an assembly or create an unregistered type? | `MultiPassMessageTypeBinder`, the generated JSON-context by-name fallback, `AuditOutboxMessageBuilder` | **Done 2026-10-10.** No arbitrary type is created: no reflection-based JSON resolver is registered anywhere, so a type name that binds to a type no registered context describes yields no type info and the message is dropped. The binder's cache was unbounded and keyed by sender-chosen names (one type has endless spellings, misses cached too); now capped (4,096 by default). In #1316. |
| 7 | Are offloaded message bodies protected at rest by default, and do the docs say what the default is? | `Whizbang.Core/Offloads`, offload docs | **Done 2026-10-10.** Encryption is opt-in (`CipherName`); AES-GCM with a per-body key, sound. A receiver accepted a claim with no cipher even when it encrypts, so whoever could write the store and send a claim could hand it a body in the clear; `RequireSealedBodies` now refuses those (off by default, so senders can be moved one at a time). The Azure Blob download size cap trusted the size the claim declares; the read now stops at that size. In #1316. Default stays opt-in: owner decision below. |
| 8 | Can unreviewed code reach a release, and is every long-lived automation credential scoped and expiring? | release-branch ruleset, `PUBLISH_WITHOUT_APPROVAL`, `RELEASE_PAT`, `DOCS_REPO_PUSH_TOKEN` | **Partly done 2026-10-10.** Already sound: no workflow-level permissions, actions pinned by SHA, no `pull_request_target`, OIDC trusted publishing, provenance before push, secret scanning with push protection. A manual `nuget-publish` took any ref; it now refuses a commit no protected branch contains (#1316). The rest are repository settings: owner decisions below. The Dependabot lock-file job restores with a write token in scope, but a fresh restore runs no package code and there are no MSBuild SDK packages, so it is accepted. |
| 9 | Does the broker stay inside the trust boundary: do the transports require credentials and TLS by default, and does nothing expose the broker to callers? | `Whizbang.Transports.AzureServiceBus`, `Whizbang.Transports.RabbitMQ` options and docs | **Done 2026-10-10.** Neither transport overrides certificate validation; Azure Service Bus is TLS only. RabbitMQ TLS follows the URI (`amqps://`), the operator's choice. No credential is logged. Nothing to fix. |
