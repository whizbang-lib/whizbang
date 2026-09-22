# A case-insensitive option for `[Indexed]`

## The gap

`[Indexed(IndexKinds.Substring)]` built its index over the bare extraction:

```
CREATE INDEX ... USING gin ((data ->> 'LastName') gin_trgm_ops);
```

A case-insensitive search does not compile to a predicate over that expression. What a repository
writes is

```csharp
e.Data.LastName.ToLower().Contains(term)
e.Data.Email.ToLower() == normalized
```

which Entity Framework translates to `lower(data ->> 'LastName') LIKE '%term%'` and
`lower(data ->> 'Email') = @normalized`. The planner will not use an index over
`(data ->> 'LastName')` to answer a predicate over `lower((data ->> 'LastName'))`: an expression
index is only chosen for the expression it was built over. So the declaration is accepted, the index
is built and maintained on every write, and the query it was declared for still reads every row.

That is the exact failure `[Indexed]` exists to remove, arrived at from the other direction, and it
is worse than having no option at all because the cost is paid and the benefit is not.

Measured in a consumer: 36 of 180 index advisories on one codebase are case-folded text, across
seven properties. Case-insensitive search is the common case for anything a person types.

## What is not the gap

Substring matching itself is already served. A trigram GIN index answers `LIKE` with any pattern, so
`Contains`, `StartsWith` and `EndsWith` are all covered by `[Indexed(IndexKinds.Substring)]`, and
`_declaredIndexServes` already recognizes that. It is worth stating because the btree story invites
the wrong conclusion: a btree serves `StartsWith` only under `text_pattern_ops` or the C collation
and never serves `EndsWith`, which is true and beside the point, because the answer for a substring
match was never the btree.

So the missing piece is the case folding alone, not the pattern shape. A predicate over
`lower((data ->> 'X'))` cannot use an index over `(data ->> 'X')` whatever the operator class. The
same is true of any other function wrapped around the extraction, `Trim()` being the one that also
turns up in practice, which is why the option belongs to the indexed expression rather than to a
kind.

## Shape (built)

An optional second constructor parameter rather than a new member of `IndexKinds`:

```csharp
public sealed class IndexedAttribute(IndexKinds kind = IndexKinds.Ordered, bool caseInsensitive = false)
```

`IndexKinds` answers *which* index; case folding is a property of the expression the index is built
over, and both kinds can carry it. Folding it into the enum would make `IndexKinds.CaseInsensitive`
a member that is not a kind, and would mean `IndexKinds.Ordered | IndexKinds.CaseInsensitive` reads as
two indexes when it is one index over a different expression.

A constructor parameter rather than a settable property, deliberately: both attributes declare `Kind`
as get-only, which is what makes `[Indexed(Kind = …)]` fail to compile, and the discovery reads
positional constructor arguments only. A settable property would revive a named-argument path that
was removed for being unreachable.

## What had to change together (all three done)

Three places, and they have to agree or the index goes unused:

1. **`JsonIndexSql.Expression`** wraps the extraction: `lower((data ->> 'X'))`. Only valid for text,
   so a case-insensitive declaration on any other type is a diagnostic rather than a silent no-op.
2. **`JsonIndexInfo`** carries the flag so the ordered and substring statements both build over the
   folded expression, and the index name distinguishes it from the unfolded one.
3. **`PerspectiveFilterIndexAnalyzer._declaredIndexServes`** recognizes that a case-insensitive
   declaration serves `ToLower()`-shaped predicates, and that an unfolded declaration does not. Right
   now the advisory keeps reporting a case-folded search after the author declares a substring index,
   which is advice with no exit.

The third is the one that makes this worth doing as one change. Without it the framework would offer
an option that works and an advisory that does not know about it.

## Verified against a real plan (done)

`JsonIndexUsageTests.TheGeneratedIndexExpression_IsTheOneAQueryUsesAsync` is the existing pattern:
run the query, read the plan, assert the index is chosen. A case-insensitive index has to be held to
the same standard, because "a string containing the word CREATE looks exactly like a working one" and
this is a case where the index builds fine and answers nothing.

## Before the option existed

A consumer records the decision with `[SuppressIndexAdvisory]` naming the reason, which is what the
first pinning of this release does. The suppression is the honest state: the search is a scan, the
framework cannot yet express the index that would fix it, and the reason says so rather than leaving
a reader to wonder why a searched column is unindexed.

## How another driver would answer these

Worth recording, because it is what decided the vocabulary. The capability names the question, and
each driver answers it with whatever it has — which is not always an index on the table.

**PostgreSQL.** `Ordered` is a btree over the extraction; `Substring` is a GIN index with
`gin_trgm_ops`; `CaseInsensitive` wraps the indexed expression in `lower(…)`. All three are pure
DDL: emit the statement and the planner finds it.

**SQLite.** `Ordered` is an ordinary index over `json_extract(data, '$.X')`, which SQLite supports as
an expression index. `Substring` is not an index on the table at all — the answer is an FTS5 virtual
table shadowing the perspective, `content='wh_per_x'`, kept in sync by triggers. Plain FTS5 is
token-based and could only serve `StartsWith`, but the **trigram tokenizer** (SQLite 3.34 and later;
the bundled build is 3.50.2 or newer) matches arbitrary substrings and folds case by default, so one
structure answers `Substring` and `CaseInsensitive` together, close to what `pg_trgm` gives.

The difference that matters is not the structure, it is that **satisfying the capability stops being
DDL**. Nothing routes a `LIKE '%x%'` to an FTS table on its own, so the read path has to rewrite the
predicate into `rowid IN (SELECT rowid FROM … WHERE … MATCH ?)`. `Whizbang.Data.Dapper.Sqlite` is
Dapper over hand-written SQL with no `ILensQuery` or `IQueryable` seam, and the Entity Framework
driver is PostgreSQL only, so there is nowhere to put that rewrite today.

This is an argument for the capability vocabulary rather than against it: the driver owns however
much machinery its answer takes, and the declaration on the model does not change either way. It
does mean the SQLite implementation is its own piece of work, gated on a query seam in that driver,
and that until then the mismatch diagnostic is what tells a SQLite consumer the capability is not
available — which is the honest answer, and the one thing better than silently building nothing.

## What the implementation found that the plan did not predict

**Only the parameterless `ToLower()` reaches the database.** Entity Framework maps `ToLower()` and
`ToUpper()` to `lower()` and `upper()` and has no mapping for `ToLowerInvariant()` or for the
overloads taking a culture: a query written with those does not scan, it throws at translation. So
the folding operators the advisory recognizes are exactly those two, and the invariant forms are
deliberately not among them — reading them as folds would attach index advice to a query that never
produces a plan.

That constrains the shape a consumer has to write, and the framework has to say so, because the CLR
analyzers actively push the other way: `ToLower()` inside a query expression trips CA1304 and CA1311,
and comparing its result trips CA1862 and RCS1155, each recommending an overload with no
translation. The attribute's documentation now names the one form that works and says why those
suggestions do not apply inside a query. Suppressing four analyzers to write a supported query is
friction worth removing later, most plausibly by a framework helper the translation understands.

**`ToUpper()` is served by nothing.** It translates, to `upper(…)`, which no declaration builds an
index over. One fold has to be the one that is built; the advisory reports the upward fold and names
`ToLower()` rather than letting the query look served.

**The collapse bug.** The first implementation read the declarations on a property as one combined
answer, so a field carrying `[Indexed]` and `[Indexed(caseInsensitive: true)]` produced a single
declaration with folding on, emitted only the folded index, and left every case-sensitive query on
that field scanning with an index plainly visible on it. This is the same failure the whole feature
exists to remove, reintroduced by the feature. It survived because the test that covered "both ways"
built the two declarations by hand and asserted the SQL rendering, never asking the discovery to
produce them from source. The test now runs the generator over a model with both attributes.

## Layering: the DDL, and why it stays put for now

`JsonIndexSql` renders PostgreSQL, and it lives in the driver-agnostic `Whizbang.Generators.Shared`.
Only the PostgreSQL generator consumes it, so moving the file is easy. Moving the layer is not:
`JsonIndexCast` is the vocabulary it renders, and the build-time diagnostics are written in terms of
it, so the agnostic analyzer project depends on a PostgreSQL-shaped answer to "can this type carry an
index, and over what expression". Relocating the renderer while the enum it renders stays behind
would move a file and leave the coupling.

The fix is a per-driver seam both the analyzer and the generator ask, which is a design change rather
than code motion, and is best done with a second driver in hand to keep it honest. It is safe to
defer: these types are internal to the generator layer, so a later move breaks no consumer, unlike
the attribute vocabulary, which is what consumers write and was therefore settled first.
