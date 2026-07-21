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
3. **Schema-qualify (the one that bites)** — service-schema objects: `__SCHEMA__.wh_*`. Shared
   **public** objects (`wh_settings`, `wh_dead_letter_summary`): leave **bare**. **Qualify inside
   function bodies too** — the runner does *not* qualify inside `$$…$$` bodies, so a bare ref there
   resolves via the caller's `search_path` at runtime and silently reads `public` on a
   service-schema connection. This is the #1 latent multi-schema bug.
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

**Rule 3 is enforced** by `scripts/Lint-MigrationSql.ps1` (CI step in the `format` job) — it fails on
any *new* bare `wh_` ref inside a function body. Existing debt (89 refs across 37 files) is baselined
in `scripts/migration-sql-lint-baseline.txt`; fix one, then rerun with `-UpdateBaseline` to ratchet
the baseline down. Other known debt: function-naming drift (rule 4), duplicate prefixes `040/042/044`
(rule 8).
