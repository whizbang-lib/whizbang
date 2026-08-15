# Whizbang SQL migrations — read before editing

**Full authoring guide:** the docs site → Contributors → Data engines →
**"Writing SQL migrations"** (`whizbang-lib.github.io` →
`src/assets/docs/contributors/data-engines/writing-migrations.md`). Companion pages:
"SQL function contracts" (which functions each engine must provide) and
`operations/infrastructure/migrations` (how the applier tracks/re-runs them).

These `.sql` files are the **single source of truth**, embedded and shared verbatim by *both*
runners — Dapper (`PostgresSchemaInitializer`) and EF Core (generated
`EnsureWhizbangDatabaseInitializedAsync`). They run on **every startup** (SHA-256 hash-skip on
unchanged); each runs in a **runner-managed transaction**. Pre-v1.0: **edit in place**, don't add a
new file for a fix.

## The rules, in one screen

1. **Idempotent** — `CREATE OR REPLACE FUNCTION`, `CREATE TABLE/INDEX IF NOT EXISTS`,
   `ADD COLUMN IF NOT EXISTS`, `DROP ... IF EXISTS`, `INSERT ... ON CONFLICT`. Never a bare
   `CREATE TABLE`/`CREATE FUNCTION`/`ADD COLUMN`/`CREATE INDEX`.
2. **ORM-agnostic** — plain PostgreSQL + the `__SCHEMA__` placeholder only. No EF/ORM DDL.
3. **Schema-qualify — always, everywhere (the one that bites)** — every framework object is
   `__SCHEMA__.wh_*`. There are **no shared public objects**; a bare reference is always a bug.
   **This includes inside `$$…$$` function bodies**, top-level DDL, index targets and
   `COMMENT ON`. Never rely on a runner to qualify a bare name for you:
   - the **Dapper** runner does exactly one thing, `sql.Replace("__SCHEMA__", schema)` — it never
     auto-qualifies anything, in bodies or at top level;
   - the **EF Core** runner additionally regex-qualifies a *hard-coded 16-name list* of legacy
     tables, with **no `$$`-body awareness at all** — so it rewrites those names everywhere, and
     every other name nowhere.

   A bare ref therefore resolves through the connection's `search_path`, which defaults to
   `"$user", public`. At migration time the object is *created* in `public`; at runtime a bare ref
   inside a function body silently *reads* `public`. Both are invisible on a single-schema
   deployment, where `__SCHEMA__` **is** `public` and the two forms are indistinguishable — which
   is why this survives local testing and CI and breaks only for consumers who partition by schema.

   `wh_settings`, `wh_log`, `wh_dead_letters` and `wh_dead_letter_summary` were bare until migration
   105 and were long described here as "shared public objects". They were not a design: they were
   written without the prefix that migration 000 had already established, and the rule was authored
   afterwards to describe the result. Sharing them is not benign — `wh_settings.setting_key` is the
   primary key, so co-located services cannot hold different values for `debug_mode` or any
   retention knob. Locked by `MigrationSchemaQualificationTests` and `Lint-MigrationSql.ps1`.

   Note the two runners also substitute `__SCHEMA__` **differently** — Dapper injects the raw name,
   the EF Core generator injects the quoted form (`"myschema"`). Anything that consumes the
   placeholder as *text* rather than as an identifier must tolerate both (see 105, which resolves
   the schema via `regclass` instead).
4. **Naming** — tables always `wh_`; the 11 contract functions keep their fixed unprefixed names
   (see SQL function contracts); *new* functions get `wh_`; internal helpers get a leading `_`.
5. **Modify a function = `CREATE OR REPLACE` the whole thing**, copied verbatim + the delta
   (Postgres has no partial-alter). Use `SELECT __SCHEMA__.drop_all_overloads('fn')` first if the
   signature changed.
6. **Data (not schema) migrations** gate on a `wh_settings` version marker, not the file hash
   (e.g. `063`).
7. **Runtime safety** — `SECURITY INVOKER` (default), `SET timezone='UTC'` for time-sensitive
   functions, `debug_mode`-guard destructive maintenance, `pg_notify`/advisory locks are the
   established primitives.
8. **Ordering** is **lexicographic on the whole filename** — zero-pad the 3-digit prefix, keep it
   **unique** (existing collisions `040/042/044` — don't add more), never renumber.
9. **Header block** (`-- Migration / Date / Description / Dependencies`) + `COMMENT ON`. Exemplar:
   `050_WhDeadLetters.sql`.
10. **Connection `search_path`** is defense-in-depth, not the contract — never rely on it alone
    (pgbouncer/Aspire/pooled EF connections strip it). Rule 3 is what makes the SQL correct.

11. **Same-object chains re-run automatically (redefinition closure)** — several files may
    redefine one object over time (the store procedures, the emit chain). When the ledger re-runs
    ANY file (new or hash-changed), it automatically re-runs every LATER file defining the same
    SQL objects, so the database always ends on each object's last-word definition. Object lists
    are extracted from each file's `CREATE`/`ALTER`/`DROP` statements; files whose definitions
    hide in dynamic SQL (or that define nothing) MUST declare them explicitly with a
    `-- Objects: name1, name2` (or `-- Objects: none`) header — a regression test fails any file
    that neither parses to an object nor declares one. The older convention of manually
    hash-bumping the last-word file still works but is no longer load-bearing; "Re-run note"
    comments are documentation now, not the mechanism.

**Rule 3 is enforced** by `scripts/Lint-MigrationSql.ps1` (CI step in the `format` job) — it fails on
any bare `wh_` ref inside a function body. The historical debt has been **burned down to zero**
(baseline empty); the lint now holds it at zero. `-Fix` auto-qualifies flagged refs; `-UpdateBaseline`
re-baselines after a reviewed change. Remaining known debt: bare **function calls** inside bodies
(e.g. `PERFORM wh_create_schedule(...)` — same class, not yet linted), top-level DDL in `009`/`031`,
function-naming drift (rule 4), duplicate prefixes `040/042/044` (rule 8).
