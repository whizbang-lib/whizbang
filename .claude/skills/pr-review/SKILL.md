---
name: pr-review
description: >-
  Review a Whizbang pull request against this repository's standards and draft the comments into the
  reviewer's pending GitHub review, never submitting it. Use when asked to review a pull request, to
  draft review comments, to check a branch before opening a pull request, or to say whether a pull
  request is ready. Covers the mechanical findings CI already reports and the judgment checks a script
  cannot make: tests that can fail, test placement, coverage, docs and tests links, AOT, SQL and
  migrations, hot paths, type names, security, public API and writing.
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

It puts each finding on its line, puts the rest in the review summary, and skips what is already
drafted. If it reports a block for another commit, CI has not finished on the head: wait for it rather
than drafting stale findings. Do not re-derive these findings by hand, and do not repeat them in the
judgment comments.

## Step 3: Judgment checks

Classify the changed files, then run every check whose trigger matches. A check whose trigger does not
match stands down silently. Each row names the document that owns the rule: read that section before
writing a comment that cites it, and cite it in the comment.

| Trigger (changed paths) | Check | Owner |
|---|---|---|
| any `src/**` change | Does the change do what the description says, and nothing it does not mention? Are failure paths handled? Does a non-rethrowing `catch` log what failed? | CONTRIBUTING.md "What the reviewer checks" |
| any `src/**` change | Would each new test fail without the change? Does it assert the behavior, not the implementation? Was the type the test names actually exercised (a `StartAsync` that returned proves only scheduling)? | `ai-docs/tdd-strict.md`, `ai-docs/flaky-tests.md` |
| `tests/**` | Is each test in the project its type requires (Unit, Component, Integration)? Any sleep, real clock, polling, or a timeout used as the wait? Async test names end in `Async`? | `testing` skill, `docs/TEST-PROJECTS.md`, `ai-docs/testing-tunit.md` |
| uncovered lines in the gate comment | Is the line reachable by a real test? An `[ExcludeFromCodeCoverage]` added: is the member wholly untestable and justified? Suggest the test that reaches the line, not an exclusion. | `ai-docs/coverage-exclusions.md` |
| new or changed public type or member in `src/**` | XML docs with `<docs>` (versionless path) and `<tests>` tags? A companion docs-site pull request for user-visible behavior? A breaking change without a deprecation? A new type with no caller in the library is not shippable. | `ai-docs/documentation-maintenance.md`, CLAUDE.md "Code-Docs Linking" |
| `src/Whizbang.Core/**` and other runtime packages | No reflection, no `Type.GetType`, AOT-safe JSON through the generated contexts. | `ai-docs/aot-requirements.md` |
| `src/*Generators*/**` | Syntactic filtering before semantic work, value-equatable models, no captured symbols. Generated names use the shared type-name helpers. | `src/Whizbang.Generators/ai-docs/performance-principles.md`, `common-pitfalls.md`, `ai-docs/type-naming.md` |
| `src/**/Migrations/*.sql` | New file follows the numbered rules: idempotent, schema-qualified, header, `COMMENT ON`, docs and tests links, shared literals as constants. | `src/Whizbang.Data.Postgres/Migrations/README.md` |
| work-coordination SQL, claiming, the commit-order stamper, schema initialization, serialization | A hot path or a startup path: is there a measurement or a stated reason? Does any statement depend on an earlier statement's committed effect? | `ai-docs/load-under-bulk-import.md`, `ai-docs/schema-initialization-connections.md` |
| DI registration, `AddWhizbang*`, startup | Does the framework ever silently do less than the consumer asked for? | `ai-docs/startup-registration-invariants.md` |
| perspective storage, temporal values | One stored unit, one reader. | `ai-docs/perspective-stored-forms.md` |
| identifiers created in `src/**` | `TrackedGuid` (or `Guid.CreateVersion7()`) rather than a random GUID, where the value is persisted or ordered. | `ai-docs/code-standards.md` "UUIDv7 for Identifiers" |
| anything, including the description and commit messages | No client, consumer, tenant, environment or person named. No secret or connection detail. Untrusted input stays untrusted; SQL takes values as parameters. | CLAUDE.md first section, `docs/security-assurance-case.md` |
| prose: comments, docs, description | en-US and no em-dashes in places the regex cannot judge (identifiers, quoted text). Conventional Commit titles. A description that says what changed and why. | CLAUDE.md "Language", CONTRIBUTING.md |
| `.github/**`, `scripts/**` | Script standards; least-privilege workflow permissions; actions pinned to a commit SHA. | `ai-docs/script-standards.md` |

**Do not invent findings.** A check with nothing to say says nothing; an empty review summary with an
approval recommendation is a valid result. Distinguish what you verified against the code from what you
suspect: a blocking comment states the failure it causes, with the input that causes it. When unsure,
phrase it as a question to the author.

**Style nits** (a blank line, a name you would have chosen differently) go in only when the repository
has a rule for them, and are marked "Nit:" so the author can tell them from what blocks.

## Step 4: Write each comment

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
Only the review whose `viewerDidAuthor` is true is yours to edit.

## Step 5: Report to the reviewer

In the conversation, not on GitHub:

- the verdict to submit with (approve, comment, or request changes) and the one-line reason;
- the blocking comments, then the rest, each with its file and line;
- anything you could not decide, as a question for the reviewer.

The reviewer submits from the pull request's "Files changed" view.
