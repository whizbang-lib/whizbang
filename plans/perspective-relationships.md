# Perspective relationships: declared, indexed, documented

## Goal

Joins between perspectives are fast by default, and the shape of a service's read models (which
model refers to which, through which field, stored how) is visible to people and to LLMs without
reading the code.

## The problem

Perspectives have no foreign keys. A join between two of them reads
`orders.data->>'CustomerId' = customers.id`. The referenced side is the primary key and is indexed.
The referencing side is extracted from the document on every candidate row, which on a large
document means detoasting the whole value, so the planner can only hash-join over a full scan or
nested-loop with nothing to seek.

## What actually makes the join fast

A `FOREIGN KEY` constraint does not. PostgreSQL does not index the referencing column for a
constraint, and the planner uses a constraint only to sharpen join selectivity estimates. A
constraint also cannot be declared over a jsonb path.

The speedup is an index on the referencing side, in increasing order of payoff:

1. An expression index on the extraction, `((data->>'CustomerId')::uuid)`. No storage change;
   `lens-full-index-coverage` established the uuid cast is immutable and indexable.
2. A physical column (`[PhysicalField]`, `Extracted`) with a btree. Avoids the detoast, stores 16
   bytes instead of text, and is the only form that can carry a constraint.
3. A constraint on top of (2). Integrity and documentation; close to nothing for reads.

Enforced constraints are hazardous in this model and are opt-in at most: perspectives are
eventually consistent and processed independently (an Order row can land before its Customer),
one perspective rebuilds without the other, purge/TTL/archival delete one side, and references
across services point into another database. The default is **declared, indexed, documented,
unenforced**.

## Workstreams

### A. Declaring a relationship

- `[References(typeof(CustomerModel))]` on the referencing property, with storage (expression index
  or physical column), enforcement (`None` default, `NotValid`, `Enforced`) and scope options.
- Inferred with no attribute when the property's type is a generated strongly-typed id whose stream
  belongs to a model. Name matching (`CustomerId` to `CustomerModel`) is a suggestion only.
- Collections (`IReadOnlyList<Guid> TagIds`, GIN) and nested references (`Lines[].ProductId`,
  jsonb path index).
- Fluent registration for models in assemblies the author does not own.
- Scope-aware: lens scopes add tenant filters, so the index leads with the scope column when the
  model is scoped.
- The SQL the lens generates for the join must match the index expression exactly (`->>`, cast),
  or the index is never reached.

### B. Discovery

- Build time: extend the `PerspectiveFilterIndexAnalyzer` walk (WHIZ302) to join key selectors,
  `GroupJoin`, correlated `SelectMany`, the two-step `ids.Contains(x.Data.FooId)` pattern, and
  `GetByIdAsync` in a loop (N+1). A code fix inserts the attribute.
- Runtime: what the analyzer cannot see (HotChocolate filters, dynamic queries). Sources are the
  observed predicates, `pg_stat_user_tables.seq_scan`, `pg_stat_statements`, and data sampling
  (uuid-shaped document values matched against other perspectives' ids, reported with a match
  rate). Emitted as a `PerspectiveRelationshipAdvised` system event and a log line carrying the
  attribute to paste, after the `PerspectiveIndexAdvised` pattern.
- Apply modes `Off | Advise | ApplyIndexes`. Only indexes are ever applied automatically, with
  `CREATE INDEX CONCURRENTLY`, once, through the maintainer duty. Default: apply in development,
  advise in production.

### C. Using relationships

- Typed lens join helpers that emit the indexed expression.
- Generated HotChocolate DataLoader resolvers from declared relationships.
- Batched `GetByIdsAsync` to replace N+1 loops.

### D. Integrity without constraints

- Orphan counts from the integrity sweep, exported as a metric.
- Rebuild ordering from the relationship graph.

### E. Not joining

- Suggest a combined perspective, subscribing to both streams, when the same join is hot; the
  relationship metadata says which embedded copies to refresh when the source changes.

### F. Structure documents

Emitted at build time into `.whizbang/`, beside `message-registry.json`, behind an MSBuild property
(`WhizbangEmitModelDocs`, default on in Debug):

- `model-graph.json`: the source of truth the others render from; the VS Code extension reads it.
- `model-structure.md`: how to read the diagrams, the nomenclature, the class-to-table mapping.
- `model-erd.mmd`: physical tables and columns. Mermaid `type name PK|FK "comment"`; the comment
  carries the jsonb path. Solid (`--`) for enforced constraints, dashed (`..`) for logical ones.
- `model-classes.mmd`: the C# models. Members tagged `[col:x]`, `[jsonb]`, `[split]`, `[idx]`,
  `[ref->Model]`; which events feed each model.

Deterministic, sorted output; split by namespace past about 40 entities; cross-service references
as stub entities; an optional live-database drift check.

## Phasing

0. Measure against a production consumer. **Done 2026-10-06**, below.
1. Fix the analyzer's silence on correlated join keys (below), and add the `Data.Id`-instead-of-`Id`
   and mismatched-reference-type rules. Smallest change with the largest measured payoff.
2. Declaration, index generation, structure documents (A, F).
3. Analyzer and code fix for relationships proper (B, build time).
4. Runtime advisor and index apply (B, runtime).
5. Query helpers and DataLoaders (C).
6. Enforcement, last and possibly never.

## Sanity check against a consumer service (2026-10-06)

Run against a copy of a consumer's development database and its source. Model and field names below
are generic stand-ins: a *parent* perspective, and a *child history* perspective whose `ParentId`
references it. The consumer-specific working files are kept outside this repository.

### What the consumer's code actually does

- Real `Join`/`GroupJoin` across perspectives: **one**, unused on request paths.
- The joins that matter are **correlated `let` subqueries** over multi-model lenses: EF emits one
  correlated scalar subquery per outer row. The worst site runs four of them per row of a paged grid,
  keyed on a Guid that has no index.
- **Two-step `Contains`** lookups (about 20 sites) and **N+1 GraphQL field resolvers** without
  DataLoaders.
- **Non-sargable correlation**: user ids stored as strings and matched with `Data.Id.ToString()`.
  No index serves that; only a type fix does. The relationship analyzer should flag a reference
  whose type differs from the target's id type.
- **`r.Data.Id` instead of `r.Id`**: the jsonb copy of the key instead of the primary key. The
  consumer had measured roughly a hundredfold from that change alone. A cheap analyzer rule.

### The existing analyzer misses join keys (bug)

`PerspectiveFilterIndexAnalyzer._containmentCanServe` treats any `==` against a non-null value as
answerable by the whole-document containment index. The runtime rewriter refuses exactly this case:
`JsonbContainmentRewriter._tryRewrite` bails when the other side references a query parameter ("has
to be a value, not a second column"). So `c.Data.ParentId == p.Data.Id` in a correlated subquery is
routed to the WHIZ307/308 whole-document check, never gets WHIZ302, and scans. That is why the
consumer's worst unindexed relationship built clean. Fixing it is the first step of workstream B,
and it is small.

### Discovery by data sampling: useful, not sufficient

`discover.sql` (kept with the working files) samples uuid-shaped top-level document keys and matches
them against every other perspective's ids. It found every populated relationship the code
inventory names. Two limits make it a supplement to the analyzer, not a replacement:

- **Sibling perspectives share a stream id.** One aggregate projected into many perspectives makes a
  `ParentId` match all of them equally. Data cannot say which one the reference means; the code can.
  The advisor must collapse same-stream families into one target.
- **Empty tables are invisible.** The consumer's three worst relationships were on tables that were
  empty in that database.

### Orphans: why constraints stay off by default

Over 80 percent of child rows reference a parent id with no row in the perspective the code joins
them to. They are not corrupt: that perspective covers a subset of the parents. An enforced foreign
key would have rejected those projection writes.

### Measured

Real query shape: a page of 50 parents, each with two correlated "latest child entry" subqueries,
plus point lookups, N+1, two-step `= any(...)`, and a whole-tenant join. Median of repeated
`EXPLAIN ANALYZE` runs on PostgreSQL 17. V0 is the database as it was (only the legacy
whole-document GIN index). V1 is an expression index exactly as `[Indexed]` generates it,
`((data ->> 'ParentId')::uuid)`. V1b adds the ordering key. V2 is a stored generated column plus
btree (`[PhysicalField]`). V3 adds a `NOT VALID` foreign key to V2.

As found (child table in the low tens of thousands of rows, one child per parent):

| Query | V0 none | V1 expr idx | V1b + order | V2 column | V3 + FK |
|---|---|---|---|---|---|
| page, 50 parents x 2 correlated lookups | 1,641 ms | 0.32 ms | 0.29 ms | 0.33 ms | 0.35 ms |
| point lookup, one parent | 3.36 ms | 0.003 ms | 0.002 ms | 0.003 ms | 0.003 ms |
| N+1, 50 sequential lookups | 156 ms | 0.15 ms | 0.10 ms | 0.12 ms | 0.13 ms |
| two-step, `= any(200 ids)` | 9.6 ms | 0.13 ms | 0.13 ms | 0.13 ms | 0.16 ms |
| whole-tenant join + aggregate | 6.2 ms | 1.9 ms | 2.0 ms | 1.9 ms | 2.1 ms |

Scaled (parents copied 20x with fresh ids, five child entries per parent: about a million child
rows, 1 GB):

| Query | V0 none | V1 expr idx | V1b + order | V2 column | V3 + FK |
|---|---|---|---|---|---|
| page, 50 parents x 2 correlated lookups | **208,096 ms** | 0.55 ms | 0.34 ms | 0.74 ms | 0.71 ms |
| point lookup, one parent | 263 ms | 0.005 ms | 0.003 ms | 0.007 ms | 0.007 ms |
| N+1, 50 sequential lookups | 8,889 ms | 0.28 ms | 0.13 ms | 0.34 ms | 0.36 ms |
| two-step, `= any(200 ids)` | 442 ms | 0.42 ms | 0.44 ms | 0.69 ms | 0.52 ms |
| whole-tenant join + aggregate | 280 ms | 85 ms | 96 ms | 149 ms | 90 ms |

The unindexed page goes from 1.6 s to 3.5 minutes as the child table grows a hundredfold; indexed,
it stays under a millisecond. The whole-tenant join improves only threefold, because a hash join
over everything was already a reasonable plan: indexes matter for selective lookups, which is what
request paths do.

### Conclusions

1. **The index is the whole win; the foreign key adds nothing to reads.** V1, V2 and V3 are within
   noise of each other on every read.
2. **The expression index is enough.** A physical column buys no measurable read speed at these
   document sizes (under 2 KB, below the TOAST threshold). Promote a column when a constraint is
   wanted or documents are large enough to be TOASTed, not for speed alone.
3. **`[Indexed]` already generates the right index.** The feature is mostly about getting it
   declared: the analyzer bug above, relationship-aware advice, and making the declaration
   discoverable. No new index machinery is needed for the common case.
4. **Correlated subqueries are where it hurts**: the cost is rows x scans, so it grows with the
   square of the data. A 50-row page over a small table already took 1.6 s unindexed.
5. **Write cost did not register** at 5,000-row batches, at either size: run-to-run noise exceeded
   any difference between variants. A one-index-per-relationship write cost is real but small next to
   the document GIN index these tables already carry.
6. **The ordering key is a modest extra.** Carrying the "latest entry" ordering in the relationship
   index (V1b) removes the sort and roughly halves the already sub-millisecond lookups. Worth offering
   when the analyzer sees `Where(key) + OrderBy + First`, not worth defaulting.
