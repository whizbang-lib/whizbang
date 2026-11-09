# Changelog fragments

A pull request into `develop` records its changelog entry here, as a file of its own, instead of editing
`CHANGELOG.md`. Every pull request writes a different file, so two pull requests never conflict over the
changelog, in the merge queue or locally. Cutting a release gathers every fragment into the version's section
of `CHANGELOG.md` and deletes them in the same commit (`docs/RELEASING.md`, "The changelog").

## The file

Name it by the issue the pull request fixes, `changelog.d/<issue>.md` (`2168.md`), or by the pull request
number when there is no issue. A change that closes several issues may use a range, `2152-2154.md`; the
gather sorts by the first number.

The file starts with front matter naming where the entry goes, then the entry itself, a markdown list item
written for a consumer of the library:

```markdown
---
category: Fixed
subsection: Storage
---
- **The thing that changed, in one sentence (#2168).** What a consumer sees now, and what they need to do,
  if anything. A breaking change ends with an **Upgrade:** note saying exactly what to change.
```

- `category` is required: one of the categories below.
- `subsection` is optional: one of the subsections below. Leave it out for an entry that fits none.
- The entry is one or more list items (`- `), wrapped at about 120 characters with two-space continuation
  lines, the same as `CHANGELOG.md`. No headings: the gather writes those from the front matter.

Anything else (another key, an unknown category or subsection, a file not named by a number) fails the
release cut, naming the file. Preview what a cut would write, from the repository root:

```bash
pwsh .github/scripts/Merge-ChangelogFragments.ps1 -Version 9.9.9 -DryRun
```

## Categories

In the order the version section lists them ([Keep a Changelog](https://keepachangelog.com/en/1.0.0/)):

- `Added`: a new capability.
- `Changed`: a change to existing behavior, including a breaking change (with its **Upgrade:** note).
- `Deprecated`: something that still works and will be removed.
- `Removed`: something that is gone.
- `Fixed`: a defect corrected.
- `Security`: a fixed vulnerability. Once its advisory is published, the entry names the `GHSA-...` id
  and says no more than the advisory does (`.claude/skills/security-advisory/SKILL.md`).

## Subsections

The areas `CHANGELOG.md` already groups entries by, in the order they are listed within a category:

- `Security`
- `Rebuilds`
- `Storage`
- `Audit`
- `Startup`
- `Messaging & delivery`
- `Work coordination, perspectives & sagas`
- `Context, identity & serialization`
- `Observability & tooling`

A new subsection is added in `.github/scripts/Merge-ChangelogFragments.ps1` and here in the same pull
request; a test fails when the two lists differ.

## Where entries go instead

- A pull request into a release branch (`release/vX.Y.Z`: a stabilization fix or a hotfix) edits
  `CHANGELOG.md` directly, under that version's own section, never under `[Unreleased]`. A fragment added
  there would wait for the next release.
- A pull request into `develop` that adds lines under `[Unreleased]` in `CHANGELOG.md` is refused by
  `Gate · Pull request type`, pointing here. Editing an already released section (a correction, or a
  `GHSA-...` id added after its advisory is published) is allowed.
