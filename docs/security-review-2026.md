# Security review 2026

**Status:** workbook, in progress. This file becomes the record of the review once the reviewer has
worked through it and filled in every **Decision** cell and the sign-off.

| | |
|---|---|
| Reviewer | _(project member performing the review)_ |
| Date completed | _(YYYY-MM-DD)_ |
| Scope | The published `SoftwareExtravaganza.Whizbang.*` packages (including the `whizbang` command-line tool) and the repository's build and release pipeline, as of `develop` on 2026-10-10 |
| Basis | The [security assurance case](security-assurance-case.md): its requirements, threat model and trust boundaries |
| Method | For each trust boundary, the STRIDE threats (Spoofing, Tampering, Repudiation, Information disclosure, Denial of service, Elevation of privilege) are checked against the controls in the code, with the code and tests that show each control; then the automated analysis results and the build and release pipeline are reviewed |

## How to use this workbook

Every row has a **Decision** cell. For each one, check the cited code (and tests where given), then
write one of:

- **Confirmed**: the control exists and does what the row says;
- **Finding**: something is missing or wrong. Give it a number in [Findings](#findings) with a severity
  and what happens next (fix, accept with reason, or defer with an issue);
- **Not applicable**, with the reason.

The rows marked **Proposed finding** are gaps noticed while preparing the workbook. They are
suggestions: confirm, reject or reword them. Every cited file was checked to exist on `develop` on
2026-10-10, and every "Control in place" was re-read against the code that day.

## A. Automated analysis results (checked 2026-10-10)

| Source | Result | Evidence (what was checked) | Decision |
|---|---|---|---|
| CodeQL (C#) | 0 open alerts; one sample-app false positive dismissed with a reason | `.github/workflows/codeql.yml`: push and pull request to `main` and `develop` (markdown-only pull requests skipped), and weekly on Sunday; runs on 2026-10-10 green. Code-scanning alerts from CodeQL: 0 open, 1 dismissed (#16, `cs/exposure-of-sensitive-information` in `samples/ECommerce/ECommerce.NotificationWorker/Receptors/SendNotificationReceptor.cs`, false positive) | |
| SonarCloud | 0 vulnerabilities, 0 security hotspots to review, 0 bugs, 0 code smells; line coverage 100%, branch coverage 98.6% (every hand-written branch outcome covered, enforced on every pull request) | SonarCloud measures and hotspot search for `whizbang-lib_whizbang`; the hand-written figure is the pull-request quality gate's whole-library row (a hard gate since #1190). Sonar's branch figure also counts compiler-generated branches | |
| Dependabot and the weekly OSV scan | 0 open Dependabot alerts; 1 open OSV alert, `braces` (stack exhaustion) in the sample UI, which is not shipped; no patched release exists (3.0.3 is the latest) | Dependabot alerts: 0 open. Code-scanning alert #347 (osv-scanner, `samples/ECommerce/ECommerce.UI/pnpm-lock.yaml`). `.github/workflows/security-supply-chain.yml`: push, pull request, and weekly on Tuesday | |
| Secret scanning (GitHub and TruffleHog) | 0 alerts | GitHub secret-scanning alerts: 0 open. `.github/workflows/security-secrets.yml` (TruffleHog) on every push and pull request; runs on 2026-10-10 green | |
| OpenSSF Scorecard | 8.4 of 10 (run of 2026-10-04). Below 10: Code-Review 0 and Branch-Protection 3 (one active maintainer, a recorded decision), CII-Best-Practices 5, Vulnerabilities 7 (the sample UI's npm advisories), Contributors 6 | `https://api.securityscorecards.dev/projects/github.com/whizbang-lib/whizbang`; `.github/workflows/security-scorecard.yml` runs weekly on `develop`. Code-scanning alert #90 (Scorecard, Vulnerabilities) open | |
| Property-based and mutation testing | Property tests run on every pull request; mutation testing on demand | FsCheck properties in the unit suite (for example `tests/Whizbang.Core.Tests/ValueObjects/TrackedGuidPropertyTests.cs`); `.github/workflows/mutation.yml` is manual dispatch only | |

## B. Trust boundaries

### B1. Broker to application (received messages)

| Threat | Control in place | Evidence | Decision |
|---|---|---|---|
| **Spoofing**: a sender claims an identity, roles or permissions it does not have | **Proposed finding.** The security context is rebuilt from the message's hop chain (scope, roles, permissions, principals, claims) and trusted as sent; nothing authenticates it (no MAC or signature anywhere in `src/Whizbang.Core/Security/`). Anyone who can publish to a queue the application reads can claim any permission, and `[RequirePermission]` checks then pass. The assurance case states this limit; the decision is whether to accept it (broker access is the trust boundary) or to authenticate hops (for example a keyed MAC over the scope delta). | `src/Whizbang.Core/Security/Extractors/MessageHopSecurityExtractor.cs`, `src/Whizbang.Core/Security/DefaultRequirePermissionInterceptor.cs`; tests `tests/Whizbang.Core.Tests/Security/MessageHopSecurityExtractorTests.cs` | |
| **Tampering**: a message body is altered in transit or at rest | Integrity of the broker hop is the transport's (TLS). An offloaded body is checked against the SHA-256 hash in its claim before use; an encrypted body also carries an AES-GCM tag bound to the cipher name and key id. | `src/Whizbang.Core/Offloads/BodyClaimRehydrator.cs`, `src/Whizbang.Core/Offloads/AesGcmEnvelopeCipher.cs`; tests `tests/Whizbang.Core.Tests/Offloads/BodyClaimRehydratorTests.cs`, `tests/Whizbang.Core.Tests/Offloads/AesGcmEnvelopeCipherTests.cs` | |
| **Elevation of privilege**: a payload makes the framework load or instantiate a type it should not | Payloads deserialize only through source-generated JSON metadata the application registers. Two places turn a type **name** from a message into a `Type`: on Azure Service Bus a registry miss falls back to a multi-pass binder (`Type.GetType`), and the generated JSON context's by-name fallback calls `Type.GetType` and then searches every loaded assembly. Deserialization still needs registered metadata, so an unregistered type goes to a raw receptor or is dropped. Check: can either `Type.GetType` call load an assembly that is not already loaded (it probes the application directory)? | `src/Whizbang.Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs` (registry-miss fallback), `src/Whizbang.Core/Messaging/MultiPassMessageTypeBinder.cs`, `src/Whizbang.Generators/Templates/Snippets/JsonContextSnippets.cs` (`GET_TYPE_INFO_BY_NAME_FALLBACK`); tests `tests/Whizbang.Core.Tests/Messaging/MultiPassMessageTypeBinderTests.cs` | |
| **Denial of service**: oversized messages exhaust consumer memory | **Proposed finding.** The payload limit (5 MiB default) is enforced only where the framework serializes a message, on the sending side (`Dispatcher`). A producer that bypasses the framework can still deliver a larger message, and the receive path has no size check of its own. Options: a receive-side check against the same limit, or document reliance on the broker's message size limit. | `src/Whizbang.Core/Configuration/WhizbangCoreOptions.cs` (`MaxMessagePayloadBytes`), `src/Whizbang.Core/Messaging/MessagePayloadLimits.cs`; tests `tests/Whizbang.Core.Tests/Messaging/MessagePayloadLimitsTests.cs` | |
| **Denial of service**: a poison message is redelivered forever | Poison detection, bounded retries, then the dead-letter queue; an unbindable message is acknowledged and dropped rather than abandoned. | `src/Whizbang.Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs` (poison gate first), dead-letter handling under `src/Whizbang.Core/DeadLetters/` | |
| **Repudiation**: no record of who sent what | Each message carries its hop chain (service, time, correlation and causation ids) and, where enabled, the audit log records it. Note: the hop chain is itself unauthenticated (see Spoofing). | Docs: `fundamentals/security/audit-logging` | |

### B2. HTTP client to application (REST, GraphQL, SignalR built with the framework's transports)

| Threat | Control in place | Evidence | Decision |
|---|---|---|---|
| **Spoofing / elevation**: an unauthenticated or unauthorized caller reads or changes data | Authentication and authorization are the application's (ASP.NET Core). The framework applies the caller's scope to reads through the scoped lens factory. Check: does every transport endpoint the framework generates read through the scoped lenses, with no path that bypasses scope? | `src/Whizbang.Core/Lenses/` (scoped lens factory); tests `tests/Whizbang.Core.Tests/Lenses/ScopedLensFactoryTests.cs`, `tests/Whizbang.Data.EFCore.Postgres.Tests/ScopedLensFactoryIntegrationTests.cs`; HotChocolate and FastEndpoints transports | |
| **Information disclosure**: a GraphQL lens returns fields its `Scope` hides | **Proposed finding (fixed).** Through 0.2612.0, a `[GraphQLLens]` returned, and allowed filtering and sorting on, the whole perspective row (`metadata`, tenancy `scope`, system fields) whatever its `Scope` declared. Fixed in 0.2614.0: each lens gets its own types containing only the declared parts. Advisory GHSA-9gmc-m5qr-hprg is drafted (affected `>= 0.4.0-alpha.1, < 0.2614.0`, patched `0.2614.0`); a CVE was requested on 2026-10-08. | Fix: #1198 (`src/Whizbang.Transports.HotChocolate.Generators/GraphQLLensTypeGenerator.cs`, `src/Whizbang.Transports.HotChocolate/Attributes/GraphQLLensScope.cs`, enum `GraphQLLensScopes`) | |
| **Information disclosure**: fields a caller may not see are returned | Field-level permissions can mask or redact fields. GraphQL field suggestions can be turned off in production. | `src/Whizbang.Core/Security/Attributes/FieldPermissionAttribute.cs`; docs `apis/graphql/production-hardening` | |
| **Denial of service**: expensive queries | Lens paging is on by default with `DefaultPageSize = 10` and `MaxPageSize = 100`, settable per lens. The framework sets no query depth or cost limit of its own; HotChocolate's defaults apply. Check: is relying on HotChocolate's defaults acceptable, and is that stated in the production-hardening docs? | `src/Whizbang.Transports.HotChocolate/Attributes/GraphQLLensAttribute.cs` (`DefaultPageSize`, `MaxPageSize`); docs `apis/graphql/production-hardening` | |

### B3. Application to database (PostgreSQL)

| Threat | Control in place | Evidence | Decision |
|---|---|---|---|
| **Tampering (SQL injection)** | Values always travel as parameters (Npgsql, Dapper, EF Core). The collective store's runtime column names are checked against an identifier pattern. Check: schema and table names come from configuration and generated code; is configuration treated as trusted, and is that stated? | `src/Whizbang.Data.Postgres/Collective/CollectiveApplyHookPlan.cs` (`IsValidIdentifier`); tests `tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveApplyHookPlanTests.cs` | |
| **Elevation of privilege**: the application's database role can do more than it needs | Startup applies migrations, which needs DDL rights. Check: is a split documented (a migrator role for DDL, a runtime role without it)? | `ai-docs/schema-initialization-connections.md`, `src/Whizbang.Data.Postgres/` connection roles | |
| **Information disclosure**: credentials leak | Npgsql removes the password from a connection string after open; credentials come from host configuration. Check: no log line prints a connection string or a message payload. | `src/Whizbang.Data.Postgres/Notifications/ConnectionStringCredentialMarkerSummary.cs` | |
| **Information disclosure**: data at rest | Event and dead-letter rows hold message payloads in plain JSON. Encryption at rest is the database's (or the application's, per field). | Assurance case, *cannot expect* | |

### B4. Application to blob store (offloaded message bodies)

| Threat | Control in place | Evidence | Decision |
|---|---|---|---|
| **Tampering**: a stored body is replaced | The claim carries a SHA-256 hash checked before use; a mismatch is a permanent failure that goes to the dead-letter queue. | `src/Whizbang.Core/Offloads/BodyClaimRehydrator.cs`; tests `tests/Whizbang.Core.Tests/Offloads/BodyClaimRehydratorTests.cs` | |
| **Information disclosure**: a stored body is read | Optional AES-256-GCM envelope encryption with a fresh data key and nonce per body, key wrapping, and key rotation that keeps the previous key for reading. **Proposed finding (low):** encryption is opt-in, so bodies are plaintext in the blob store by default; decide whether to keep that default and say so in the offload docs. | `src/Whizbang.Core/Offloads/AesGcmEnvelopeCipher.cs`, `src/Whizbang.Core/Offloads/IMessageBodyKeyWrapper.cs` (`LocalAesKeyWrapper`), `src/Whizbang.Core/Offloads/RotatingAesKeyWrapper.cs`; tests `tests/Whizbang.Core.Tests/Offloads/AesGcmEnvelopeCipherTests.cs`, `tests/Whizbang.Core.Tests/Offloads/RotatingAesKeyWrapperTests.cs` | |

### B5. Repository to user (build and release)

| Threat | Control in place | Evidence | Decision |
|---|---|---|---|
| **Tampering with dependencies** | Committed lock files restored in locked mode; Dependabot and OSV. | `Directory.Packages.props`, the `packages.lock.json` files, `.github/workflows/security-supply-chain.yml` | |
| **Tampering with build steps** | Every third-party action pinned to a commit SHA; workflow tokens read-only by default with writes granted per job. | `.github/workflows/*.yml` | |
| **Stolen publishing credential** | NuGet publishing uses OIDC trusted publishing: there is no long-lived API key to steal, and nuget.org accepts a push only from the named workflow. | `.github/workflows/nuget-push.yml` | |
| **Stolen automation token** | **Proposed finding.** Two long-lived personal access tokens are repository secrets: `RELEASE_PAT` (used by `ci.yml`, `release.yml`, `start-release.yml`, `reusable-sync-develop.yml`, `readme-refresh.yml`) and `DOCS_REPO_PUSH_TOKEN` (used by `ci.yml` and `security-advisories-docs.yml` to push to the docs repository). They exist so automated pull requests trigger CI, which `GITHUB_TOKEN` pull requests do not. Check each token's scope (repositories and permissions) and expiry; decide whether to replace them with a GitHub App's short-lived installation tokens. | `.github/workflows/*.yml` (`secrets.RELEASE_PAT`, `secrets.DOCS_REPO_PUSH_TOKEN`) | |
| **Unverifiable packages** | Stable releases carry a Sigstore-signed SLSA provenance statement; verification steps are in SECURITY.md (tested against v0.2605.0, including a tampered copy that fails). | `SECURITY.md#verifying-a-release` | |
| **Unreviewed code reaching main** | Pull requests and the required CI check on `develop` and `main`, merge queue on `develop`, no bypass actors in any ruleset, 2FA required for the organization, DCO sign-off on every commit. | Repository rulesets "Require PR for protected branches" and "Require PR for main branch"; `.github/workflows/ci.yml` (`ci-result`, `dco`) | |
| **Unreviewed code reaching a release branch** | **Proposed finding.** The ruleset "Protect release branches" (`refs/heads/release/**`) blocks deletion and force-push only: it requires neither a pull request nor the CI check, so a change can land on `release/*` without checks and ships in that branch's prereleases before the release PR's checks run. Options: require a pull request and the CI result on `release/*`. | Ruleset "Protect release branches" (rules: `deletion`, `non_fast_forward`; no bypass actors) | |
| **No human gate on publishing** | **Proposed finding.** The repository variable `PUBLISH_WITHOUT_APPROVAL=true` stands down the `nuget-publish` approval, so merging a release PR publishes to nuget.org with no human approval step. Mitigated by the required checks and trusted publishing; decide whether to restore the approval for stable releases. | `docs/RELEASING.md` (*Standing the gate down*) | |
| **A look-alike package published under the project's prefix** | The `SoftwareExtravaganza.*` package-id prefix reservation was requested from nuget.org on 2026-10-08; every package names the same author (#1245). Until it is granted, another account can publish under the prefix. | nuget.org search API (`verified: false` on 2026-10-10) | |
| **A single person holds every key** | Governance names the continuity requirement; a backup maintainer is not yet designated. | `GOVERNANCE.md#continuity` | |

### B6. Command-line tool to the internet (`whizbang audit`)

| Threat | Control in place | Evidence | Decision |
|---|---|---|---|
| **Information disclosure**: the tool sends project details to a third party | `whizbang audit` sends only Whizbang package ids and the versions a project resolves, to `api.osv.dev` over HTTPS, when a user runs it. No credentials, no other package names, no project paths. | `tools/Whizbang.CLI/Audit/OsvClient.cs`; tests `tests/Whizbang.CLI.Tests/Audit/OsvClientTests.cs`, `tests/Whizbang.CLI.Component.Tests/Audit/AuditCommandTests.cs` | |
| **Spoofing / tampering**: a forged or unreadable answer gives a false all-clear | HTTPS to a fixed host; a non-success status, an unreadable record, a result count that does not match the queries, or the 15-second deadline are each reported as "could not check" (exit 2), never as "no advisories". Requests are anonymous, so a draft advisory is never visible to it. | `tools/Whizbang.CLI/Audit/OsvClient.cs`, `tools/Whizbang.CLI/Audit/AuditCommand.cs`; tests as above | |

## C. Cross-cutting checks

| Check | Notes | Decision |
|---|---|---|
| Cryptography | AES-256-GCM from the .NET runtime, keys from a CSPRNG. MD5 and SHA-1 only derive stable identifiers, each with a written justification: partition-key GUIDs (`DapperPostgresPerspectiveStore.cs`, `EFCorePostgresPerspectiveStore.cs`) and RFC 4122 version-5 saga item ids (`SagaItemStreams.cs`). | |
| Secrets in the repository | None; secret scanning on every push (Table A). | |
| Unsafe code and native interop | A search of `src/` on 2026-10-10 found no `unsafe` blocks and no `DllImport`, `LibraryImport` or `extern` methods. | |
| Reflection at runtime | Not only the type binder. Runtime type resolution by name: `MultiPassMessageTypeBinder.cs` and the generated JSON context's by-name fallback (both B1), and `AuditOutboxMessageBuilder.cs` (resolves an event's stored type name to read its audit attribute; logs and falls back on failure). Instance creation: `EFCoreInfrastructureRegistration.cs` uses `Activator.CreateInstance` for closed generic stores and queries over model types registered at startup, not over input. Everything else is source-generated (Native AOT). | |
| Error messages and logs | Check that exception messages and logs never include payload contents, credentials or personal data. | |

## Findings

_Numbered findings agreed during the review: number, title, severity (high, medium, low), decision
(fix, accept, defer), and the issue or pull request that tracks it._

| # | Finding | Severity | Decision | Tracking |
|---|---|---|---|---|
| | | | | |

## Sign-off

_"I have reviewed the security requirements and boundaries described in the assurance case against
the code and pipeline as listed above."_ Name, date.
