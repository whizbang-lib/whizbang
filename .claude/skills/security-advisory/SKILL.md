---
name: security-advisory
description: >-
  How to write and publish a Whizbang security advisory: the GHSA fields, the CVE description form,
  choosing CWE and scoring CVSS, affected and patched version ranges, and the wording rules that keep an
  advisory true years after it is published. Use whenever the task involves a security advisory, GHSA,
  CVE, CVSS, CWE, a vulnerability report, co-ordinated disclosure, or the security section of the docs.
---

# Writing a Whizbang security advisory

An advisory is a **permanent record**. Dependabot, OSV, SBOM tooling and humans read it years after it is
published, in a repository that has moved on. Everything below follows from that.

Repository advisories: <https://github.com/whizbang-lib/whizbang/security/advisories>.
The reporting channel and response commitments live in `SECURITY.md` — do not restate them in an advisory.

## 1. An advisory is timeless

Never describe the project's current moment. Describe the affected set.

| Don't | Do |
|---|---|
| "Whizbang is pre-1.0, and 1.0 will be the first production-ready release." | "All affected versions are 0.x pre-releases." |
| "This is not yet fixed." | Leave the patched-version field empty. |
| "Currently only the Dapper path is affected." | "The Dapper work coordinator is affected; the EF Core path is not." |
| "A fix will ship in the next release." | "Fixed in 0.2615.0." |

Banned words in advisory prose: **currently, now, today, recently, not yet, soon, upcoming, will be,
still**. If a point in time genuinely matters (disclosure date, embargo lift), write the absolute date.

A forward-looking promise is worse than vague: it is a claim that goes false on its own. "1.0 will be the
first production-ready release" is wrong the day 1.0 ships, and wrong differently if 1.0 is renumbered.

## 2. Version ranges live in the structured fields, not the prose

GitHub's affected-versions and patched-version fields are what becomes the OSV record
(`introduced` / `fixed` events) that scanners act on. Prose that repeats a range drifts out of step with
the metadata, and the metadata is the half that is machine-read.

- Set the ecosystem (`NuGet`), the affected package(s), the affected range and the patched version.
- Whizbang ships many packages from one repo. List **every** affected package id, not just
  `SoftwareExtravaganza.Whizbang.Core` — a consumer who only references the transport or data package must
  still match.
- Say "all affected versions are 0.x pre-releases" in prose only as a *property of the set*, never as a
  statement about the project.
- The one version prose may carry is the upgrade instruction, "Upgrade to <first patched version> or
  later.", because it stays true after every later release. It must name exactly the version in the
  patched field; never restate the affected range.

## 3. The description follows the CVE form

The CVE Program's description template is the house style, because it forces the useful facts into one
sentence and carries no time reference:

> `[VULNTYPE]` in `[COMPONENT]` of `[PRODUCT]` `[VERSION]` allows `[ATTACKER]` to `[IMPACT]` via
> `[VECTOR]`.

Then expand, in this order:

1. **Impact** — what an attacker gets. Who is at risk, and under what configuration. A reader decides
   whether to drop everything based on this paragraph; put it first.
2. **Affected configurations** — the setting, attribute or code path that makes a deployment vulnerable,
   and the one that does not. Be explicit about what is *not* affected; it is as load-bearing as what is.
3. **Mechanism** — why it happens, at the level of the design decision that was wrong. Enough for a
   reviewer to confirm the fix addresses the cause.
4. **Workarounds** — a configuration change that mitigates without upgrading, or "None; upgrade to
   <version> or later."
5. **Credit** — the reporter, by the name they chose, unless they asked not to be named.

**No proof of concept, no exploit steps, no working payload.** Describe the class of problem and the
affected path. A reviewer needs the mechanism; an attacker must not be handed the recipe.

## 4. Severity and classification are required

- **CWE** — pick the most specific identifier that fits, not the broadest. `CWE-200` (exposure of
  sensitive information) is right for a disclosure; reach for `CWE-863`/`CWE-862` when the defect is an
  authorization decision, `CWE-89` for injection, and so on.
- **CVSS** — score it and keep the vector string. Score the vulnerability in a **default** deployment; if a
  non-default setting is required to be vulnerable, that belongs in the affected-configurations paragraph,
  not in a quietly lowered score.
- Severity words in prose must agree with the score. A "critical" paragraph over a medium vector reads as
  carelessness in both directions.

## 5. A Whizbang advisory has repository duties too

An advisory is not finished when it reads well:

- **Draft first, publish after the fix is on NuGet.** Publishing a GHSA makes it public immediately and
  feeds Dependabot; an advisory that lands before the patched package exists tells every consumer they are
  vulnerable with nowhere to go.
- **The patched version must be a real, published version**, not the one the release is expected to get.
- Fix it on `develop` like any other defect, and reference the advisory from the PR. Do not describe the
  vulnerability in the commit message before the advisory is public — the commit is public the moment it
  is pushed.
- Request a CVE through GitHub once the advisory is ready to publish, if the defect affects a published
  package.
- Add the advisory's `GHSA-…` id to the release notes for the version that fixes it.

## 6. The docs site renders the published advisories; nobody maintains a list by hand

The documentation site's security page is **generated** from the OSV record, never typed: a build step
queries `api.osv.dev` for every published package id (unauthenticated, so a draft advisory can never reach
it), commits the result as data, and the page renders from that. The page says "as of <date>" and links
the live list, <https://github.com/whizbang-lib/whizbang/security/advisories>, so a snapshot can be
behind GHSA but never contradict it. A failed fetch keeps the last committed data and warns; it never
fails the build or renders an empty list.

So publishing an advisory needs no docs change: once GHSA exports it to OSV, the next generator run
picks it up. Never add an advisory to the docs by hand; a hand-maintained list is the §1 failure one level
up, going stale silently while contradicting the machine-readable record.

## Checklist before publishing

- [ ] No banned time words; no forward-looking promises.
- [ ] Affected range and patched version set in the structured fields; prose restates neither, beyond
      "Upgrade to <patched version> or later."
- [ ] Every affected package id listed, not just `Whizbang.Core`.
- [ ] Impact paragraph first; what is *not* affected stated explicitly.
- [ ] CWE as specific as the defect allows; CVSS vector recorded and consistent with the prose.
- [ ] No proof of concept or exploit steps.
- [ ] Reporter credited as they asked.
- [ ] The patched version is published on NuGet.
- [ ] The release notes for that version name the `GHSA-…` id.
