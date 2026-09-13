# A case-insensitive option for `[Indexed]`

## The gap

`[Indexed(IndexKinds.Trigram)]` builds its index over the bare extraction:

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

## Shape

An optional second constructor parameter rather than a new member of `IndexKinds`:

```csharp
public sealed class IndexedAttribute(IndexKinds kind = IndexKinds.Btree, bool caseInsensitive = false)
```

`IndexKinds` answers *which* index; case folding is a property of the expression the index is built
over, and both kinds can carry it. Folding it into the enum would make `IndexKinds.CaseInsensitive`
a member that is not a kind, and would mean `IndexKinds.Btree | IndexKinds.CaseInsensitive` reads as
two indexes when it is one index over a different expression.

A constructor parameter rather than a settable property, deliberately: both attributes declare `Kind`
as get-only, which is what makes `[Indexed(Kind = …)]` fail to compile, and the discovery reads
positional constructor arguments only. A settable property would revive a named-argument path that
was removed for being unreachable.

## What has to change together

Three places, and they have to agree or the index goes unused:

1. **`JsonIndexSql.Expression`** wraps the extraction: `lower((data ->> 'X'))`. Only valid for text,
   so a case-insensitive declaration on any other type is a diagnostic rather than a silent no-op.
2. **`JsonIndexInfo`** carries the flag so the btree and trigram statements both build over the
   folded expression, and the index name distinguishes it from the unfolded one.
3. **`PerspectiveFilterIndexAnalyzer._declaredIndexServes`** recognizes that a case-insensitive
   declaration serves `ToLower()`-shaped predicates, and that an unfolded declaration does not. Right
   now the advisory keeps reporting a case-folded search after the author declares a trigram index,
   which is advice with no exit.

The third is the one that makes this worth doing as one change. Without it the framework would offer
an option that works and an advisory that does not know about it.

## Verified against a real plan

`JsonIndexUsageTests.TheGeneratedIndexExpression_IsTheOneAQueryUsesAsync` is the existing pattern:
run the query, read the plan, assert the index is chosen. A case-insensitive index has to be held to
the same standard, because "a string containing the word CREATE looks exactly like a working one" and
this is a case where the index builds fine and answers nothing.

## Until then

A consumer records the decision with `[SuppressIndexAdvisory]` naming the reason, which is what the
first pinning of this release does. The suppression is the honest state: the search is a scan, the
framework cannot yet express the index that would fix it, and the reason says so rather than leaving
a reader to wonder why a searched column is unindexed.
