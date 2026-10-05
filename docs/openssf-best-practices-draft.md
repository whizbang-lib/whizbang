# OpenSSF Best Practices: passing-level answers

The answers entered for the **passing** level of the OpenSSF Best Practices badge (Metal series,
<https://www.bestpractices.dev/criteria/0>) in the project's entry at <https://www.bestpractices.dev/>.
This file is the record of what the entry says: when an answer changes on the site, change it here too.

**Status**: passing badge earned (project [15090](https://www.bestpractices.dev/projects/15090), badge in the README).

Every claim was checked against the repository, GitHub and the
docs site on 2026-09-27. Criteria are in the order the form presents them; the 67 listed are every
current passing criterion (retired and future criteria are left out).

Base URLs used below:

- Repository: `https://github.com/whizbang-lib/whizbang` (default branch `develop`)
- Docs site: `https://whizba.ng/docs/...` (a single-page app: deep links answer HTTP 404 with a
  redirect shell and render correctly in a browser)

## Project information (General)

| Field | Entered |
|---|---|
| Badge series | Metal (passing, silver, gold): the series OpenSSF Scorecard reads |
| Name | Whizbang |
| Brief description | A .NET library for event-driven, CQRS and event-sourced applications. Handlers, routing, read models and storage are wired by source generators at compile time: no reflection, Native AOT from day one. |
| Language | English (en) |
| Project URL | <https://whizba.ng> |
| Repository URL | <https://github.com/whizbang-lib/whizbang> |
| License | MIT |
| Programming languages | C#, PLpgSQL, PowerShell, JavaScript, Shell (the site's auto-fill also listed TSQL, a misdetection of `.sql` files) |
| CPE name | none |
| Other comments | Pre-1.0. Stable releases ship as 0.x.0 on nuget.org under the SoftwareExtravaganza.Whizbang.* package IDs, with alpha builds from develop in between. |

Every Justification cell below is the text entered on the site, word for word (for
`repo_public`, `repo_track` and `repo_distributed`, the site's own auto-filled text was kept).

## Fixed before submitting

Found while verifying the answers and fixed on 2026-09-27, so the pages the answers link to are
accurate:

1. CodeQL alert #16 (a sample application logging the `NotificationType` enum member `Email`) was
   dismissed as a false positive; no static-analysis finding is open.
2. `SECURITY.md` states the support policy (the latest stable release) instead of a stale version
   table.
3. GitHub Discussions is enabled, and `CONTRIBUTING.md` sends questions and feature ideas there and
   bugs to issues. It no longer mentions issue or pull request templates that do not exist, links the docs site at
   `whizba.ng`, and describes how pull requests are actually reviewed and merged.
4. The repository homepage is set to `https://whizba.ng`.
5. GitHub detects the license as MIT.

## Note on human review

No passing-level criterion requires a second person to review changes. The review criteria start at
silver (`code_review_standards`) and gold (`two_person_review`). If the form or a reviewer asks,
the answer is **Unmet**: there is one active maintainer, and a pull request author cannot approve
their own pull request. Every change goes through automated gates (the full test matrix, 100% of
new lines covered, zero SonarCloud findings, CodeQL) and AI review, and none of those counts as
human review.

---

## Basics

### Basic project website content

| Criterion | Answer | Justification |
|---|---|---|
| `description_good` | Met | The README opens with what the library is for: a .NET library for event-driven, CQRS and event-sourced applications, wired by source generators at compile time with no reflection and Native AOT support. <https://github.com/whizbang-lib/whizbang#readme> |
| `interact` | Met | The README links the docs site, NuGet packages, releases and contributing guide; CONTRIBUTING covers asking questions and suggesting features (GitHub Discussions), reporting bugs (issues) and sending changes (pull requests). <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md> |
| `contribution` | Met | CONTRIBUTING describes the process: fork, branch per the gitflow naming, Conventional Commits, open a pull request into `develop`, CI must pass. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#submitting-changes> |
| `contribution_requirements` | Met | CONTRIBUTING lists the standards a contribution must meet (code style, XML docs, AOT rules, TUnit tests, `dotnet format` clean, no warnings, lock files updated) and links the detailed guides. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#standards-and-guidelines> |

### FLOSS license

| Criterion | Answer | Justification |
|---|---|---|
| `floss_license` | Met | Released under the MIT license. <https://github.com/whizbang-lib/whizbang/blob/develop/LICENSE> |
| `floss_license_osi` | Met | MIT is OSI-approved. <https://opensource.org/license/mit> |
| `license_location` | Met | The license is the top-level `LICENSE` file. <https://github.com/whizbang-lib/whizbang/blob/develop/LICENSE> |

### Documentation

| Criterion | Answer | Justification |
|---|---|---|
| `documentation_basics` | Met | The docs site covers installation, a quick start, and each feature, including security guidance (message security, HTTP security headers, GraphQL production hardening, encrypted message bodies). <https://whizba.ng/docs/getting-started/installation>, <https://whizba.ng/docs/fundamentals/security/message-security> |
| `documentation_interface` | Met | Public APIs carry XML documentation shipped in the packages (`GenerateDocumentationFile` in `Directory.Build.props`; in the core and generator packages a missing doc comment fails the build), and the docs site documents each public surface. <https://whizba.ng/docs/getting-started/introduction> |

### Other

| Criterion | Answer | Justification |
|---|---|---|
| `sites_https` | Met | The repository (GitHub), docs site (`https://whizba.ng`, HTTP redirects to HTTPS) and packages (nuget.org) are served over HTTPS only. |
| `discussion` | Met | GitHub Discussions (Q&A, Ideas, Announcements) plus issues and pull requests: searchable, addressable by URL, open to new participants, no proprietary client needed. <https://github.com/whizbang-lib/whizbang/discussions> |
| `english` | Met | All documentation, code comments and issue discussion are in English (US spelling is a documented standard). |
| `maintained` | Met | Actively developed: pull requests merge into `develop` most days, and stable releases ship regularly; the releases page lists each one with its date. <https://github.com/whizbang-lib/whizbang/releases> |

## Change Control

### Public version-controlled source repository

| Criterion | Answer | Justification |
|---|---|---|
| `repo_public` | Met | Repository on GitHub, which provides public git repositories with URLs. |
| `repo_track` | Met | Repository on GitHub, which uses git. git can track the changes, who made them, and when they were made. |
| `repo_interim` | Met | Interim work is public: `develop` carries every change between releases, and each merge publishes an alpha package. <https://github.com/whizbang-lib/whizbang/blob/develop/docs/RELEASING.md> |
| `repo_distributed` | Met | Repository on GitHub, which uses git. git is distributed. |

### Unique version numbering

| Criterion | Answer | Justification |
|---|---|---|
| `version_unique` | Met | Every release gets a unique version, decided once by GitVersion from the highest tag and kept identical from preview to publish to tag. <https://github.com/whizbang-lib/whizbang/blob/develop/docs/RELEASING.md#how-the-version-is-decided> |
| `version_semver` | Met | Versions follow Semantic Versioning `MAJOR.MINOR.PATCH`, with prerelease suffixes `-alpha.N`, `-beta.N` and `-rc.N`; the changelog states SemVer adherence. <https://github.com/whizbang-lib/whizbang/blob/develop/CHANGELOG.md> |
| `version_tags` | Met | Each release is tagged in git as `vMAJOR.MINOR.PATCH`. <https://github.com/whizbang-lib/whizbang/tags> |

### Release notes

| Criterion | Answer | Justification |
|---|---|---|
| `release_notes` | Met | Every GitHub release carries notes listing the changes by pull request, and `CHANGELOG.md` summarizes notable changes. <https://github.com/whizbang-lib/whizbang/releases>, <https://github.com/whizbang-lib/whizbang/blob/develop/CHANGELOG.md> |
| `release_notes_vulns` | N/A | No publicly known vulnerability has been reported against or fixed in the project; there are no published security advisories. <https://github.com/whizbang-lib/whizbang/security/advisories> |

## Reporting

### Bug-reporting process

| Criterion | Answer | Justification |
|---|---|---|
| `report_process` | Met | Bugs are reported as GitHub issues; CONTRIBUTING says what to include. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#reporting-bugs> |
| `report_tracker` | Met | GitHub issues. <https://github.com/whizbang-lib/whizbang/issues> |
| `report_responses` | Met | Every issue opened by someone other than the maintainer in the last twelve months (13 of them) has been responded to and closed. <https://github.com/whizbang-lib/whizbang/issues?q=is%3Aissue> |
| `enhancement_responses` | Met | Enhancement requests use the same tracker, and every one opened in the last twelve months was answered and resolved. <https://github.com/whizbang-lib/whizbang/issues?q=is%3Aissue> |
| `report_archive` | Met | The issue tracker is public and searchable, with full history. <https://github.com/whizbang-lib/whizbang/issues?q=is%3Aissue> |

### Vulnerability report process

| Criterion | Answer | Justification |
|---|---|---|
| `vulnerability_report_process` | Met | `SECURITY.md` explains how to report a vulnerability. <https://github.com/whizbang-lib/whizbang/blob/develop/SECURITY.md> |
| `vulnerability_report_private` | Met | Reports go privately through GitHub's "Report a vulnerability" flow (private vulnerability reporting is enabled on the repository). <https://github.com/whizbang-lib/whizbang/security/advisories/new> |
| `vulnerability_report_response` | Met | `SECURITY.md` commits to an initial response within 48 hours. No vulnerability reports have been received in the last six months. <https://github.com/whizbang-lib/whizbang/blob/develop/SECURITY.md> |

## Quality

### Working build system

| Criterion | Answer | Justification |
|---|---|---|
| `build` | Met | `dotnet build Whizbang.slnx` builds everything from source; CI runs it on every pull request. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#setting-up-your-development-environment> |
| `build_common_tools` | Met | The standard .NET SDK and MSBuild. |
| `build_floss_tools` | Met | The .NET SDK, MSBuild and Roslyn are open source (MIT). |

### Automated test suite

| Criterion | Answer | Justification |
|---|---|---|
| `test` | Met | More than 26,000 automated tests (TUnit) across unit, generator, integration and transport suites, published in the repository. <https://github.com/whizbang-lib/whizbang/tree/develop/tests> |
| `test_invocation` | Met | Tests run with the standard `dotnet test` (or `dotnet run` per project); `scripts/Run-Tests.ps1` wraps the common modes. <https://github.com/whizbang-lib/whizbang/blob/develop/docs/TEST-FILTERING.md> |
| `test_most` | Met | Line coverage is 100% on SonarCloud, and the pull request gate requires every added library line to be executed by a test. <https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang> |
| `test_continuous_integration` | Met | The full test matrix (unit, PostgreSQL, RabbitMQ, Azure Service Bus, Azure Blob, in-memory) runs on every pull request and again in the merge queue before anything reaches `develop`. <https://github.com/whizbang-lib/whizbang/actions/workflows/ci.yml> |

### New functionality testing

| Criterion | Answer | Justification |
|---|---|---|
| `test_policy` | Met | The project's documented policy is test-driven development: tests are written before the implementation (RED, GREEN, REFACTOR), and new code must be covered. <https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/tdd-strict.md>, <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#testing-standards> |
| `tests_are_added` | Met | Enforced by CI: the pull request gate fails unless 100% of new lines are covered, so functional changes ship with tests (for example PR #900 added 33 test files with its change). <https://github.com/whizbang-lib/whizbang/pull/900> |
| `tests_documented_added` | Met | The testing standards and the pull request requirements in CONTRIBUTING state that changes come with tests, and the coverage gate is documented for contributors. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#pull-request-requirements>, <https://whizba.ng/docs/contributors/new-code-coverage> |

### Warning flags

| Criterion | Answer | Justification |
|---|---|---|
| `warnings` | Met | Every project builds with nullable reference types, .NET analyzers at `latest-recommended`, code-style enforcement in the build, and Roslynator and SonarAnalyzer rules. <https://github.com/whizbang-lib/whizbang/blob/develop/Directory.Build.props> |
| `warnings_fixed` | Met | `TreatWarningsAsErrors` and `CodeAnalysisTreatWarningsAsErrors` are on, so a warning fails the build; CI builds in Release with the full analyzer set. <https://github.com/whizbang-lib/whizbang/blob/develop/Directory.Build.props> |
| `warnings_strict` | Met | Warnings are errors, analysis level is `latest-recommended`, and code style is enforced at build time. <https://github.com/whizbang-lib/whizbang/blob/develop/Directory.Build.props> |

## Security

### Secure development knowledge

| Criterion | Answer | Justification |
|---|---|---|
| `know_secure_design` | Met | The primary developer applies least privilege and fail-safe defaults throughout: workflow tokens default to read-only with writes granted per job, message bodies are encrypted with authenticated encryption that binds key and cipher identity, and security scoping is enforced at the query layer. <https://whizba.ng/docs/fundamentals/security/security> |
| `know_common_errors` | Met | The primary developer designs against the common vulnerability classes for this kind of software (injection, unsafe deserialization, secret leakage, supply chain): parameterized SQL, source-generated AOT-safe serialization, secret scanning, locked dependency restores. <https://github.com/whizbang-lib/whizbang/blob/develop/SECURITY.md> |

### Use basic good cryptographic practices

| Criterion | Answer | Justification |
|---|---|---|
| `crypto_published` | Met | The only security cryptography is AES-256-GCM (message-body encryption and data-key wrapping), a published, reviewed algorithm. <https://github.com/whizbang-lib/whizbang/blob/develop/src/Whizbang.Core/Offloads/AesGcmEnvelopeCipher.cs> |
| `crypto_call` | Met | The library calls the platform's `System.Security.Cryptography` (`AesGcm`, `RandomNumberGenerator`) and implements no cryptographic primitive itself. |
| `crypto_floss` | Met | All cryptography comes from the open-source .NET runtime. |
| `crypto_keylength` | Met | Keys are 256-bit; key encryption keys of any other length are rejected at construction. <https://github.com/whizbang-lib/whizbang/blob/develop/src/Whizbang.Core/Offloads/IMessageBodyKeyWrapper.cs> |
| `crypto_working` | Met | Security relies only on AES-256-GCM. MD5 and SHA-1 are used by design as non-security hash functions that turn a key into a stable identifier (RFC 4122 version 5 identifiers are defined on SHA-1); nothing relies on them for secrecy, integrity or authentication. |
| `crypto_weaknesses` | Met | AES-GCM with a fresh random 96-bit nonce and a fresh data key per body; no mode or algorithm with known serious weaknesses. |
| `crypto_pfs` | N/A | The library implements no key-agreement protocol; transport security is provided by the underlying clients (Npgsql, RabbitMQ, Azure SDKs) over TLS. |
| `crypto_password_storage` | N/A | The library does not store passwords for authenticating external users. |
| `crypto_random` | Met | Keys and nonces come from `RandomNumberGenerator`, the platform CSPRNG. <https://github.com/whizbang-lib/whizbang/blob/develop/src/Whizbang.Core/Offloads/AesGcmEnvelopeCipher.cs> |

### Secured delivery against man-in-the-middle (MITM) attacks

| Criterion | Answer | Justification |
|---|---|---|
| `delivery_mitm` | Met | Packages are delivered over HTTPS by nuget.org, which signs them with its repository signature; stable GitHub releases also attach Sigstore-signed SLSA provenance and SHA-256 checksums. <https://github.com/whizbang-lib/whizbang/releases> |
| `delivery_unsigned` | Met | No hash is retrieved over HTTP; dependency content hashes are pinned in committed `packages.lock.json` files and CI restores with `--locked-mode`. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#package-versions-and-lock-files> |

### Publicly known vulnerabilities fixed

| Criterion | Answer | Justification |
|---|---|---|
| `vulnerabilities_fixed_60_days` | Met | No open Dependabot alerts and no unpatched public vulnerabilities of medium or higher severity; Dependabot and a weekly OSV supply-chain scan watch for new ones. <https://github.com/whizbang-lib/whizbang/actions/workflows/security-supply-chain.yml> |
| `vulnerabilities_critical_fixed` | Met | Same process; vulnerable dependencies are fixed as soon as they are reported. The most recent was in a library the integration-test tooling pulls in, which no shipped package depends on; the test projects were pinned to its patched version in PR #910. <https://github.com/whizbang-lib/whizbang/pull/910> |

### Other security issues

| Criterion | Answer | Justification |
|---|---|---|
| `no_leaked_credentials` | Met | No valid private credentials are in the repository; TruffleHog scans every push and pull request, and GitHub secret scanning has no alerts. <https://github.com/whizbang-lib/whizbang/actions/workflows/security-secrets.yml> |

## Analysis

### Static code analysis

| Criterion | Answer | Justification |
|---|---|---|
| `static_analysis` | Met | CodeQL, SonarCloud and the Roslyn analyzers (.NET, Roslynator, SonarAnalyzer) run on every pull request, and the pull request gate requires zero new SonarCloud findings. <https://github.com/whizbang-lib/whizbang/actions/workflows/codeql.yml>, <https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang> |
| `static_analysis_common_vulnerabilities` | Met | CodeQL's C# security queries and SonarCloud's vulnerability and hotspot rules target common vulnerability classes. SonarCloud reports 0 vulnerabilities and 0 open security hotspots. |
| `static_analysis_fixed` | Met | Findings are fixed before merge: the pull request gate blocks any open SonarCloud finding, and there are no open static-analysis findings. |
| `static_analysis_often` | Met | Static analysis runs on every pull request and push to `develop` and `main`, plus a weekly scheduled CodeQL run. |

### Dynamic code analysis

| Criterion | Answer | Justification |
|---|---|---|
| `dynamic_analysis` | Met | FsCheck property-based tests generate inputs for identifier ordering, derivation, metadata and serialization invariants on every pull request; Stryker.NET mutation testing checks that the tests catch injected faults. <https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/testing-tunit.md#property-tests-with-fscheck>, <https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/mutation-testing.md> |
| `dynamic_analysis_unsafe` | N/A | The software is written in C#, a memory-safe language. |
| `dynamic_analysis_enable_assertions` | Met | Runtime checks are always on, not compiled out: argument guards and invariant exceptions run in every build, and the property tests assert invariants directly on generated inputs. |
| `dynamic_analysis_fixed` | Met | A failing property or mutation test blocks the pull request, so anything dynamic analysis finds is fixed before merge; it has found no vulnerabilities. |

---

# Silver level

The silver criteria (<https://www.bestpractices.dev/criteria/1>), in the order of the silver form
(project 15090, level 1). Silver needs passing plus every silver MUST; a SHOULD or SUGGESTED may be
Unmet with a justification. Assessed against the repository on 2026-10-04.

**Status**: every MUST is Met except `access_continuity`, which needs the owner (see below). The other rows can be entered today.

## Silver: what is left

| Criterion | Kind | What closes it | Who |
|---|---|---|---|
| `access_continuity` | MUST | A second trusted person able to create and close issues, merge, and release within a week: owner on the GitHub organization, co-owner on the nuget.org packages, access to the docs domain, named in `GOVERNANCE.md` (section Continuity). Today the organization has one member and one admin. | owner only |
| `bus_factor` | SHOULD | Unmet until a second regular contributor exists. | Unmet, justified |
| `version_tags_signed` | SUGGESTED | Release tags are created unsigned by the release workflow; leave Unmet or sign tags in the workflow. | optional |

## Basics

| Criterion | Answer | Justification |
|---|---|---|
| `achieve_passing` | Met | Passing badge earned. <https://www.bestpractices.dev/projects/15090> |
| `contribution_requirements` | Met | CONTRIBUTING lists the standards a contribution must meet and links the detailed guides. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#standards-and-guidelines> |
| `dco` | Met | Every commit must carry a Developer Certificate of Origin sign-off from its author, and a required check fails any pull request commit without one. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#developer-certificate-of-origin> |
| `governance` | Met | A maintainer-led model; the governance document says where each kind of decision is made and who makes it. <https://github.com/whizbang-lib/whizbang/blob/develop/GOVERNANCE.md> |
| `code_of_conduct` | Met | The Contributor Covenant 2.1 in the standard location, with a private reporting address. <https://github.com/whizbang-lib/whizbang/blob/develop/CODE_OF_CONDUCT.md> |
| `roles_responsibilities` | Met | The governance document lists each role (maintainer, release manager, security contact, conduct enforcement, contributor), its responsibilities and who holds it. <https://github.com/whizbang-lib/whizbang/blob/develop/GOVERNANCE.md#roles> |
| `access_continuity` | Unmet | Unmet for now. One maintainer currently holds the GitHub organization, the nuget.org packages and the docs domain. The governance document defines what a backup maintainer must hold to keep the project running (owner access to the GitHub organization, co-owner access on nuget.org, and the docs domain) and will name them once designated. <https://github.com/whizbang-lib/whizbang/blob/develop/GOVERNANCE.md#continuity> |
| `bus_factor` | Unmet | One maintainer does most of the work today, so the bus factor is 1. The governance document describes how a regular contributor takes on maintainer responsibilities. <https://github.com/whizbang-lib/whizbang/blob/develop/GOVERNANCE.md#roles> |
| `documentation_roadmap` | Met | The roadmap covers the next twelve months: what is planned toward 1.0 and after it, and what is not planned. <https://github.com/whizbang-lib/whizbang/blob/develop/ROADMAP.md> |
| `documentation_architecture` | Met | The introduction describes the architecture (dispatcher, receptors, perspectives, lenses, and how the packages fit together), and each fundamentals page documents its part. <https://whizba.ng/docs/getting-started/introduction> |
| `documentation_security` | Met | The security assurance case ends with what an application can and cannot expect from the library. <https://github.com/whizbang-lib/whizbang/blob/develop/docs/security-assurance-case.md#what-you-can-and-cannot-expect> |
| `documentation_quick_start` | Met | Quick start guide. <https://whizba.ng/docs/getting-started/quick-start> |
| `documentation_current` | Met | Public APIs link to their docs pages and docs pages to their tests, the links are validated, and a pull request that changes a public API updates its docs in the same change. Known doc defects are tracked as issues and fixed like code. <https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/documentation-maintenance.md> |
| `documentation_achievements` | Met | The README shows and links the OpenSSF Best Practices and OpenSSF Scorecard badges. <https://github.com/whizbang-lib/whizbang#readme> |
| `accessibility_best_practices` | Met | The docs site is structured with headings, every image has alt text, interactive components carry ARIA attributes, and it supports light and dark themes; the library itself has no user interface. <https://whizba.ng> |
| `internationalization` | N/A | The library produces no text for end users: its output is data, logs and diagnostics for developers and operators. |
| `sites_password_security` | N/A | The project sites (GitHub, nuget.org, the static docs site) do not store passwords of their own; authentication is GitHub's and nuget.org's. |

## Change Control

| Criterion | Answer | Justification |
|---|---|---|
| `maintenance_or_update` | Met | The upgrade path is a package version bump: each release lists its changes, and schema changes are applied automatically by the framework's migrations at startup. <https://github.com/whizbang-lib/whizbang/releases>, <https://whizba.ng/docs/data/schema-migration> |

## Reporting

| Criterion | Answer | Justification |
|---|---|---|
| `report_tracker` | Met | GitHub issues. <https://github.com/whizbang-lib/whizbang/issues> |
| `vulnerability_report_credit` | N/A | No vulnerability reports were resolved in the last twelve months. |
| `vulnerability_response_process` | Met | `SECURITY.md` documents the process: private report, response within 48 hours, confirm and scope, audit for similar problems, fix, release, and credit the reporter. <https://github.com/whizbang-lib/whizbang/blob/develop/SECURITY.md> |

## Quality

| Criterion | Answer | Justification |
|---|---|---|
| `coding_standards` | Met | The C# style guide is `ai-docs/code-standards.md` together with `.editorconfig`; CONTRIBUTING requires contributions to follow it. <https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/code-standards.md> |
| `coding_standards_enforced` | Met | Style is enforced in the build (`EnforceCodeStyleInBuild`, analyzers as errors) and by a CI job that fails on any `dotnet format` change. |
| `build_standard_variables` | N/A | No native binaries are built; the output is managed .NET assemblies. |
| `build_preserve_debug` | Met | Every package ships portable symbols in a `.snupkg` with embedded sources. |
| `build_non_recursive` | Met | MSBuild builds the solution from its project reference graph, not by recursing into directories. |
| `build_repeatable` | Met | Builds are deterministic, and CI proves it: the release path rebuilds the tested commit and fails if the binaries' hashes differ from the tested build. |
| `installation_common` | Met | Installed and removed with NuGet (`dotnet add package` / `dotnet remove package`). |
| `installation_standard_variables` | Met | NuGet decides where packages are written and honors its standard settings (`NUGET_PACKAGES`, `nuget.config`). |
| `installation_development_quick` | Met | Clone, `dotnet build`, and `pwsh scripts/Run-Tests.ps1`; integration suites start their databases and brokers in containers. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#setting-up-your-development-environment> |
| `external_dependencies` | Met | Every dependency and version is listed in `Directory.Packages.props`, with resolved versions and hashes in committed `packages.lock.json` files. <https://github.com/whizbang-lib/whizbang/blob/develop/Directory.Packages.props> |
| `dependency_monitoring` | Met | Dependabot and a weekly OSV scan check dependencies for known vulnerabilities. <https://github.com/whizbang-lib/whizbang/actions/workflows/security-supply-chain.yml> |
| `updateable_reused_components` | Met | All third-party code comes in as NuGet packages managed centrally, so updating one is a one-line change; nothing is vendored. |
| `interfaces_current` | Met | Obsolete-API warnings are build errors (`TreatWarningsAsErrors`), so deprecated APIs cannot be introduced. |
| `automated_integration_testing` | Met | The full test matrix runs on every pull request and in the merge queue, and reports success or failure per suite. <https://github.com/whizbang-lib/whizbang/actions/workflows/ci.yml> |
| `regression_tests_added50` | Met | 114 of the 137 bug-fix merges in the six months to 2026-10-04 added or changed tests (83%); the TDD policy requires a failing test before a fix. |
| `test_statement_coverage80` | Met | Line coverage is 100% on SonarCloud. <https://sonarcloud.io/dashboard?id=whizbang-lib_whizbang> |
| `test_policy_mandated` | Met | The written policy is strict TDD: tests first, and every added library line covered. <https://github.com/whizbang-lib/whizbang/blob/develop/ai-docs/tdd-strict.md> |
| `tests_documented_added` | Met | CONTRIBUTING's pull request process says to write tests and the requirements include coverage of new code. <https://github.com/whizbang-lib/whizbang/blob/develop/CONTRIBUTING.md#pull-request-process> |
| `warnings_strict` | Met | Warnings are errors, analysis level is `latest-recommended`, and Roslynator and SonarAnalyzer rules run in every project. <https://github.com/whizbang-lib/whizbang/blob/develop/Directory.Build.props> |

## Security

| Criterion | Answer | Justification |
|---|---|---|
| `implement_secure_design` | Met | Least privilege (read-only workflow tokens, writes per job), fail-safe defaults (authenticated encryption that binds key and cipher identity; tenant scope enforced at the query layer), and complete mediation of message types through a compile-time registry. |
| `crypto_weaknesses` | Met | Security relies only on AES-256-GCM; no default mechanism depends on a weak algorithm or mode. |
| `crypto_algorithm_agility` | Met | Message-body encryption is pluggable: the cipher is registered by name and recorded on every sealed body, so a deployment can switch ciphers and still read older bodies. |
| `crypto_credential_agility` | Met | Keys and connection strings come from the host's configuration (environment, secret store, key vault), never from code, and are replaced without recompiling; key rotation keeps the previous key for reading. <https://whizba.ng/docs/fundamentals/offloads/message-body-store> |
| `crypto_used_network` | Met | The library has no network protocol of its own; it talks to databases and brokers through their standard clients, which use TLS as the connection string and endpoint require (Azure Service Bus is TLS only). |
| `crypto_tls12` | Met | TLS comes from the .NET runtime, which negotiates TLS 1.2 or later. |
| `crypto_certificate_verification` | Met | Certificate verification is the .NET default and no shipped package overrides it. |
| `crypto_verification_private` | Met | Same: verification happens before any data is sent, and nothing disables it. |
| `signed_releases` | Met | Every stable release is signed keylessly with Sigstore by the release workflow (no private key exists on any distribution site), and SECURITY.md explains how to verify a GitHub release asset and a nuget.org package. <https://github.com/whizbang-lib/whizbang/blob/develop/SECURITY.md#verifying-a-release> |
| `version_tags_signed` | Unmet | Release tags are created by the automated release workflow and are not signed. The releases themselves are signed: every stable release carries a Sigstore-signed provenance statement covering each package. <https://github.com/whizbang-lib/whizbang/blob/develop/SECURITY.md#verifying-a-release> |
| `input_validation` | Met | Incoming messages deserialize only through JSON metadata registered at compile time (an allowlist); a type with no registered metadata is never deserialized, payload size is limited, and public APIs validate their arguments. |
| `hardening` | Met | Native AOT and trimming (no reflection), nullable reference types, warnings as errors, authenticated encryption, and payload size limits. |
| `assurance_case` | Met | Threat model, trust boundaries, secure design principles applied, and the common weaknesses countered. <https://github.com/whizbang-lib/whizbang/blob/develop/docs/security-assurance-case.md> |

## Analysis

| Criterion | Answer | Justification |
|---|---|---|
| `static_analysis_common_vulnerabilities` | Met | CodeQL's C# security queries and SonarCloud's vulnerability rules run on every pull request. |
| `dynamic_analysis_unsafe` | N/A | The software is written in C#, a memory-safe language. |
