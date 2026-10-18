---
name: pr-review
description: >-
  Review a Whizbang pull request against this repository's standards and draft the comments into the
  reviewer's pending GitHub review, never submitting it. Use when asked to review a pull request, to
  draft review comments, to check a branch before opening a pull request, or to say whether a pull
  request is ready. Covers the mechanical findings CI already reports, the core principles every change
  carries (no reflection and AOT, the docs repository updated, 100% line and branch coverage, code,
  docs and tests linked, generators on the template system, reuse and extensibility, deterministic
  tests on public completion signals, turn-key defaults, configuration bound with defaults, errors and
  configuration on dedicated and directory pages, docs to the site's standards with visuals and
  tested samples), and the area checks for SQL, hot paths, startup, generators,
  security and writing.
argument-hint: "<pull request number, or a branch name to review before opening one>"
---

# Reviewing a Whizbang pull request

A review here has two halves. CI already makes the mechanical half: the "Report · Review checks"
comment and the PR quality gate comment list sign-offs, closing keywords, the target branch, edited
migrations, spelling, em-dashes, real time in tests, `Guid.NewGuid()` in the library, uncovered new
lines and branches, and Sonar findings. This skill drafts those as they are and spends its own effort
on the judgment half, which the standards documents own. CONTRIBUTING.md "Code Review" is the summary a
contributor reads; this skill is how a review applies it.

**The output is a pending review, never a submitted one.** Every comment is added to the reviewer's own
pending review on GitHub, written with their `gh` login on their machine. The reviewer reads it, edits
it and submits it. Never submit, approve or request changes, and never post a plain PR comment.

## Step 1: Gather

```bash
gh pr view <n> --json number,title,body,author,baseRefName,headRefName,headRefOid,isCrossRepository,closingIssuesReferences,files
gh pr diff <n>
```

Read every changed file **in full** at the head commit, not just the hunks: a diff line without the
code around it misleads. For a change to a public type, also read its callers and its tests. Note
whether the author is the maintainer or an outside contributor; the comments are written for that
reader.

For a branch with no pull request yet (a contributor checking their own work), diff it against
`origin/develop` and report in the conversation instead of drafting a review.

## Step 2: Draft the mechanical findings

If the head commit's CI has finished, both comments carry a hidden findings block. Draft them:

```bash
pwsh scripts/Invoke-PrReviewDraft.ps1 -PullRequest <n> -WhatIf   # the plan
pwsh scripts/Invoke-PrReviewDraft.ps1 -PullRequest <n>           # starts or extends the pending review
```

It puts each finding on its line, puts the rest in the review summary (or prints them to paste at
submit, when the pending review has no summary yet), and skips what is already drafted. If it reports a block for another commit, CI has not finished on the head: wait for it rather
than drafting stale findings. Do not re-derive these findings by hand, and do not repeat them in the
judgment comments.

## Step 3: The core principles, on every pull request

These apply to every change that touches `src/**` or `tests/**`, whatever the files are. They are not
optional and not triggered: walk all eleven, and a breach of any is a blocking comment unless the
description gives a reason the maintainer has accepted.

1. **No reflection; AOT compatible.** No reflection, `Type.GetType`, `Activator`, `dynamic` or
   reflection-based JSON in any library or sample project; types are discovered by a generator and JSON
   goes through the generated contexts. Only the generator projects may use reflection. Owner:
   `ai-docs/aot-requirements.md`.
2. **The docs repository is updated.** Any new or changed public API, option, diagnostic, behavior a
   consumer can observe, or operational procedure has a companion pull request on the docs site
   (`whizbang-lib.github.io`, targeting `develop`), linked from this one. "The page should say X" in a
   description is a finding, not a substitute. Owner: `ai-docs/documentation-maintenance.md`.
3. **100% of new lines and 100% of new branches are covered,** and the whole-library hand-written branch
   gate stays at 100%. Every outcome of every new decision is taken by a test, not just every line.
   An exclusion is member-level, rare, and justified in place. Owner: `ai-docs/coverage-exclusions.md`.
4. **Code, docs and tests are linked.** Every public type and member carries `<docs>` (versionless path)
   and `<tests>` tags that resolve; every SQL function carries the same links; the docs page links back
   to the code; the code-docs and code-tests maps on the docs site are regenerated
   (`generate-code-docs-map.mjs`, `generate-code-tests-map.mjs`). Owner: CLAUDE.md "Code-Docs
   Linking" and "Code-Tests Linking".
5. **Generators use the template system.** Generated code comes from real C# template files with
   region replacement and snippets, not `StringBuilder` or string concatenation. A change that adds
   emitted code to a generator still built by concatenation is the moment to move it, or the
   description says why not (Boy Scout rule). Owner: `src/Whizbang.Generators/ai-docs/template-system.md`.
6. **Reuse and extensibility.** The change uses the helper that already exists (type names through
   `TypeNameUtilities` / `TypeNameFormatter`, identifiers through `TrackedGuid`, the shared SQL
   constants) rather than a local copy, and does not duplicate logic another type owns. New behavior a
   consumer might need to vary is an extension point (an option, an interface, a hook) with a sensible
   default, not a hard-coded branch; nothing is sealed, internal or private in a way that blocks the
   obvious extension without a reason. Owners: `ai-docs/type-naming.md`, `ai-docs/boy-scout-rule.md`.
7. **Tests are deterministic and wait on completion signals.** No `Task.Delay`, `Thread.Sleep`,
   polling loop, real clock, or timeout used as the wait; time is a `FakeTimeProvider`. Each
   asynchronous assertion waits on the signal for the exact transition it asserts. When the library had
   no signal for that transition and the change adds one, it is a **public** event, callback or hook
   with XML docs, `<docs>` and `<tests>`, and docs-site coverage: useful to a consumer for monitoring,
   never a test-only internal or an `InternalsVisibleTo` back door. Owners: `testing` skill,
   `ai-docs/flaky-tests.md`.
8. **Turn-key by default.** A new capability works from `AddWhizbang()` and the driver registration
   with no extra wiring and safe defaults; a consumer opts out, not in, unless the description says why
   opting in is safer. Registration is idempotent and never silently does less than asked. Owners:
   `ai-docs/startup-registration-invariants.md`, docs site `data/turnkey-initialization`.
9. **All configuration has a binding and a default.** Every new options class or property binds from
   configuration under a documented section (naming the concrete type at the call site, so the binder
   generator intercepts it and AOT holds), every property has a default that works, and code set
   before or after binding follows the framework's precedence. Owners:
   `src/Whizbang.Core/Configuration/ProcessWideOptionsBinding.cs` remarks, docs site
   `operations/configuration/configuration-reference`.
10. **Errors and configuration are documented twice.** Each new configuration key, runtime error
    message and diagnostic has its own detail (a dedicated page or section) and an entry on the
    directory page for quick lookup: `operations/configuration/configuration-reference` for keys,
    `operations/diagnostics/error-reference` for runtime errors, and an `operations/diagnostics/whizNNN`
    page plus the diagnostics index for analyzer codes. Both link to each other and to the code.
11. **Docs meet the docs-site standards.** Required frontmatter (`title`, `category`, `order`,
    `description`, and `pageType` / `verifiedAgainstCommit` where the section uses them), heading
    hierarchy and markdown rules; visuals wherever they explain more than prose (Mermaid diagrams for
    flows and structure, a GIF or video for an interactive or tooling flow, alt text on every image);
    and complete C# samples taken from tests, with the test named in the code block metadata so the
    sample is proven to compile and behave as described. Review the companion docs pull request against
    these, not just its existence. Owners (docs repository): `DOCUMENTATION-STANDARDS.md`,
    `ai-docs/standards.md`, `ai-docs/mermaid-diagrams.md`, `ai-docs/alt-text-standards.md`,
    `ai-docs/CODE-TEST-LINKING.md`.

## Step 4: Area checks

Classify the changed files, then run every check whose trigger matches. A check whose trigger does not
match stands down silently. Each row names the document that owns the rule: read that section before
writing a comment that cites it, and cite it in the comment.

| Trigger (changed paths) | Check | Owner |
|---|---|---|
| any `src/**` change | Does the change do what the description says, and nothing it does not mention? Are failure paths handled? Does a non-rethrowing `catch` log what failed? A new type with no caller in the library is not shippable. | CONTRIBUTING.md "What the reviewer checks" |
| any `src/**` change | Would each new test fail without the change? Does it assert the behavior, not the implementation? Was the type the test names actually exercised (a `StartAsync` that returned proves only scheduling)? | `ai-docs/tdd-strict.md`, `ai-docs/flaky-tests.md` |
| `tests/**` | Is each test in the project its type requires (Unit, Component, Integration)? Async test names end in `Async`? | `testing` skill, `docs/TEST-PROJECTS.md`, `ai-docs/testing-tunit.md` |
| uncovered lines or branches in the gate comment | Suggest the test that reaches the line or takes the outcome, not an exclusion. | `ai-docs/coverage-exclusions.md` |
| new or changed public type or member in `src/**` | A breaking change without a deprecation? | `ai-docs/documentation-maintenance.md` |
| `src/*Generators*/**` | Syntactic filtering before semantic work, value-equatable models (arrays, not lists), no captured symbols. | `src/Whizbang.Generators/ai-docs/performance-principles.md`, `value-type-records.md`, `common-pitfalls.md` |
| `src/**/Migrations/*.sql` | New file follows the numbered rules: idempotent, schema-qualified, header, `COMMENT ON`, shared literals as constants. | `src/Whizbang.Data.Postgres/Migrations/README.md` |
| work-coordination SQL, claiming, the commit-order stamper, schema initialization, serialization | A hot path or a startup path: is there a measurement or a stated reason? Does any statement depend on an earlier statement's committed effect? | `ai-docs/load-under-bulk-import.md`, `ai-docs/schema-initialization-connections.md` |
| DI registration, `AddWhizbang*`, startup | Does the framework ever silently do less than the consumer asked for? | `ai-docs/startup-registration-invariants.md` |
| perspective storage, temporal values | One stored unit, one reader. | `ai-docs/perspective-stored-forms.md` |
| anything, including the description and commit messages | No client, consumer, tenant, environment or person named. No secret or connection detail. Untrusted input stays untrusted; SQL takes values as parameters. | CLAUDE.md first section, `docs/security-assurance-case.md` |
| prose: comments, docs, description | en-US and no em-dashes in places the regex cannot judge (identifiers, quoted text). Conventional Commit titles. A description that says what changed and why. | CLAUDE.md "Language", CONTRIBUTING.md |
| `.github/**`, `scripts/**` | Script standards; least-privilege workflow permissions; actions pinned to a commit SHA. | `ai-docs/script-standards.md` |

**Do not invent findings.** A check with nothing to say says nothing; an empty review summary with an
approval recommendation is a valid result. Distinguish what you verified against the code from what you
suspect: a blocking comment states the failure it causes, with the input that causes it. When unsure,
phrase it as a question to the author.

**Style nits** (a blank line, a name you would have chosen differently) go in only when the repository
has a rule for them, and are marked "Nit:" so the author can tell them from what blocks.

## Step 5: Write each comment

One comment per finding, on the line it is about, in this shape:

- **What** is wrong, in one sentence.
- **Why** it matters in this repository: what breaks, or which rule it is, with the owning document.
- **What to do**: the concrete change, as a `suggestion` block when it is a small edit to that line.

Write for the author: plain, specific, no sarcasm. Thank an outside contributor once, in the summary.
Use the same spelling and punctuation rules the code follows.

Add a comment to the pending review (the line must be in the diff; otherwise put it in the summary):

```bash
# the reviewer's pending review id (Invoke-PrReviewDraft.ps1 creates it if none exists)
gh api graphql -f query='query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){pullRequest(number:$n){id reviews(states:PENDING,first:5){nodes{id viewerDidAuthor body}}}}}' \
  -f o=whizbang-lib -f r=whizbang -F n=<n>

# one thread on the new side of the diff
gh api graphql -f query='mutation($rv:ID!,$p:String!,$l:Int!,$b:String!){addPullRequestReviewThread(input:{pullRequestReviewId:$rv,path:$p,line:$l,side:RIGHT,body:$b}){thread{id}}}' \
  -f rv=<PRR_id> -f p=<path> -F l=<line> -f b="<body>"
```

To add to the summary, read the current body first and write it back with the addition
(`updatePullRequestReview` replaces the whole body; the draft script's summary lines must survive).
A pending review started in the browser has no body, and GitHub refuses to give it one until it is
submitted ("Could not edit a review with a missing body"): give the reviewer the summary text to paste
into the submit dialog instead, as the draft script does. Only the review whose `viewerDidAuthor` is
true is yours to edit.

## Step 6: Report to the reviewer

In the conversation, not on GitHub:

- the verdict to submit with (approve, comment, or request changes) and the one-line reason;
- the blocking comments, then the rest, each with its file and line;
- anything you could not decide, as a question for the reviewer.

The reviewer submits from the pull request's "Files changed" view.
