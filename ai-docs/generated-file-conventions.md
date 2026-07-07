# Whizbang Generated-File Conventions (`.whizbang/` vs `.whizbang-generated/`)

Whizbang writes two kinds of on-disk artifact into every project that uses the generators. They
have **opposite git intent**, so they live in two different, clearly-named folders. The folder name
tells you whether to commit the file or ignore it.

| Folder | Contents | Git intent |
|--------|----------|------------|
| `.whizbang-generated/` | Generated `.cs` (compiler output) and `message-registry.json` (VSCode tooling catalog) | **Ignore** — regenerated from source on every build |
| `.whizbang/` | `pinned-type-ledger.json` — the pinned-type rename lockfile | **Commit** — source of truth, not reconstructable from current source |

### Why the split

`message-registry.json` is derived entirely from the current source on every compile, so it is
disposable. `pinned-type-ledger.json` records the **history** of type renames (`formerNames` /
aliases) that lets old serialized events still deserialize after a type is renamed. That history
**cannot be re-derived** from the current source — if it isn't committed, a fresh clone regenerates
an empty ledger and every recorded rename alias is lost. The lockfile only does its job if it is in
version control and shared with the team.

Both files used to live in `.whizbang/`, which was blanket-ignored — so the ledger was silently
ignored too. This convention fixes that: the regenerable registry moved to `.whizbang-generated/`,
and `.whizbang/` is now reserved for committed files.

### Self-documenting `README.md`

The Whizbang build drops a `README.md` into each folder describing what it holds and whether to
commit or ignore it (the `.whizbang/README.md` is committed; the `.whizbang-generated/README.md` is
ignored). To turn this off, set `WhizbangEmitFolderReadmes=false` in your project (or
`Directory.Build.props`).

## `.gitignore` for projects that consume Whizbang

Add this to your consuming application's `.gitignore`:

```gitignore
# Whizbang: regenerable artifacts (generated .cs + message-registry.json) — ignore
**/.whizbang-generated/

# Whizbang: stale/legacy copy of the regenerable registry — ignore any leftover
**/.whizbang/message-registry.json

# NOTE: do NOT ignore .whizbang/ itself — it holds the committed
# pinned-type-ledger.json rename lockfile, which MUST be committed.
```

Then **commit** `.whizbang/pinned-type-ledger.json` for every project that has `[PinnedId]` types.

## One-time migration (existing consumers, e.g. moving to this generator version)

If you were on an older Whizbang where `message-registry.json` lived in `.whizbang/`, do this once
per repo after updating the Whizbang generator package:

1. **Update** the `SoftwareExtravaganza.Whizbang.Generators` package and **rebuild** — this
   regenerates `message-registry.json` into `.whizbang-generated/` and leaves a stale copy in
   `.whizbang/`.
2. **Update `.gitignore`** as shown above (ignore `.whizbang-generated/`, stop blanket-ignoring
   `.whizbang/`).
3. **Remove the stale registry**: delete `**/.whizbang/message-registry.json` on disk, and if a
   previous version had committed it, `git rm --cached` it.
4. **Commit the ledger(s)**: `git add **/.whizbang/pinned-type-ledger.json`. If your old
   `.gitignore` blanket-ignored `.whizbang/`, these ledgers were never committed — committing them
   now restores your rename history to version control. Review the `formerNames` entries before
   committing.
5. Commit.

## VSCode extension

The Whizbang VSCode extension reads `message-registry.json` for IntelliSense. It looks in
`.whizbang-generated/` (new location) with a fallback to `.whizbang/` (legacy), so it works across
the transition. Keep the extension updated alongside the generator package.
