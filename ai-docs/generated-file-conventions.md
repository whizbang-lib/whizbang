# Whizbang File Conventions (the `.whizbang/` folder)

Whizbang writes its on-disk artifacts into a single `.whizbang/` folder per project. Within it, git
intent is expressed by location: files at the **root** are committed source-of-truth; everything in
the **`cache/` subfolder** is regenerable and ignored.

```
.whizbang/
├── pinned-type-ledger.json   # COMMITTED — rename lockfile (source of truth)
├── README.md                 # COMMITTED — self-documents this folder
└── cache/                    # IGNORED — regenerated every build
    ├── <generated .cs>
    └── message-registry.json
```

| Location | Contents | Git intent |
|----------|----------|------------|
| `.whizbang/` (root) | `pinned-type-ledger.json`, `README.md` | **Commit** — source of truth, not reconstructable from source |
| `.whizbang/cache/` | generated `.cs`, `message-registry.json` | **Ignore** — regenerated from source on every build |

### Why the split

`message-registry.json` and the generated `.cs` are derived entirely from the current source on
every compile, so they're disposable. `pinned-type-ledger.json` records the **history** of type
renames (`formerNames` / aliases) that lets old serialized events still deserialize after a type is
renamed. That history **cannot be re-derived** from the current source — if it isn't committed, a
fresh clone regenerates an empty ledger and every recorded rename alias is lost. The lockfile only
does its job if it is in version control and shared with the team.

Both kinds of file used to live directly in `.whizbang/`, which was blanket-ignored — so the ledger
was silently ignored too. This convention fixes that: regenerable output moved into `.whizbang/cache/`,
and the `.whizbang/` root is committed.

Note both kinds are technically *generated* by Whizbang — the distinction that matters is
**committed vs disposable**, not generated vs hand-written. That's why the ignored folder is
`cache/`, not `generated/`.

### Self-documenting `README.md`

The Whizbang build drops a single committed `README.md` at the `.whizbang/` root describing what the
folder holds and which parts to commit vs ignore. Turn it off with `WhizbangEmitFolderReadmes=false`
(in your project or `Directory.Build.props`).

## `.gitignore` for projects that consume Whizbang

Add this one line to your consuming application's `.gitignore`:

```gitignore
# Whizbang: regenerable output (generated .cs + message-registry.json) — ignore.
# Everything else in .whizbang/ (the pinned-type-ledger.json lockfile + README) is committed.
**/.whizbang/cache/

# Optional: drop any stale registry left by older Whizbang versions that wrote it to .whizbang/ directly.
**/.whizbang/message-registry.json
```

Then **commit** `.whizbang/` (the ledger + README) for every project that has `[PinnedId]` types.

## One-time migration (existing consumers, e.g. moving to this generator version)

If you were on an older Whizbang where `message-registry.json` (and generated `.cs`) lived directly
in `.whizbang/`, do this once per repo after updating the Whizbang generator package:

1. **Update** the `SoftwareExtravaganza.Whizbang.Generators` package and **rebuild** — regenerable
   output now lands under `.whizbang/cache/`, and a stale `message-registry.json` may remain at the
   `.whizbang/` root.
2. **Update `.gitignore`** to `**/.whizbang/cache/` (replacing any older `**/.whizbang/` or
   `**/.whizbang-generated/` rule).
3. **Remove the stale registry**: delete `**/.whizbang/message-registry.json` on disk, and if a
   previous version committed it, `git rm --cached` it.
4. **Commit the ledger(s)**: `git add **/.whizbang/pinned-type-ledger.json`. If your old `.gitignore`
   blanket-ignored `.whizbang/`, these ledgers were never committed — committing them now restores
   your rename history to version control. Review the `formerNames` entries before committing.
5. Commit.

## VSCode extension

The Whizbang VSCode extension reads `message-registry.json` for IntelliSense. It looks in
`.whizbang/cache/` (current location) with a fallback to the legacy `.whizbang/message-registry.json`,
so it works across the transition. Keep the extension updated alongside the generator package.
