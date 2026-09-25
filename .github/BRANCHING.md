# Branching

The branching rules, the release channels and every flow the pipeline supports live in one place:
**[docs/RELEASING.md → Supported flows](../docs/RELEASING.md#supported-flows)**.

In short:

- Everyday work (`feat/*`, `fix/*`, `test/*`, `ci/*`, `docs/*`, ...) → PR into `develop`. Develop
  publishes an **alpha** on every merge.
- A release is cut with **Actions → Start Release** as `release/vX.Y.Z`. That branch publishes
  **beta** and **rc** on request (**Actions → Release Prerelease**) and takes fixes by PR from
  `fix/*`, `bugfix/*` or `hotfix/*`.
- Only a `release/vX.Y.Z` branch ever targets `main`. Merging it publishes **stable**.
- A hotfix is a `release/vX.Y.Z` branch cut from the tag it patches.

The `Gate · Gitflow branch direction` check (`.github/workflows/git-flow-check.yml`) refuses any
other direction. The list of allowed directions in that workflow and the table in RELEASING.md must
change together.
