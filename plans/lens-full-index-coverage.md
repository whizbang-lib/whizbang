# Full index coverage for lens filters

## Goal

Every field a lens can filter on is answerable from an index, for equality, for a range, and for an
ordering. No exceptions by type.

## Why this is achievable

Two mechanisms, each with a different job. The mistake to avoid is expecting either to do the other's.

**GIN containment** answers equality only. An inverted index returns "these rows contain this token";
there is no ordered answer space to ask `> x` of, and GIN has no ordered scan at all. No storage
format changes that. What a format does decide is whether the matching token can be constructed,
which is the only reason dates were excluded before.

**A btree index on the extraction** answers equality, ranges and ordering. It needs two things: the
extraction expression must be immutable, and its ordering must agree with the ordering the caller
means.

Measured, by asking PostgreSQL to build each index:

| Extraction | Index |
|------------|-------|
| `(data ->> 'X')` (text) | yes |
| `((data ->> 'X')::int)`, `::bigint`, `::numeric`, `::float8`, `::bool`, `::uuid` | yes |
| `((data ->> 'X')::timestamptz)` | **refused**, the cast is stable |
| `((data ->> 'X')::date)` | **refused**, the cast is stable |

So every type already reaches a btree except the date and time family, and those fail only because of
the format they are stored in. Store a date in a form an immutable cast reaches, and it joins the
same mechanism as everything else.

`ContainmentTypeEligibilityProbeTests.AnExtractionCanCarryABtreeIndexOnlyWhenItsCastIsImmutableAsync`
holds that measurement.

## The linchpin, verified

A canonical fixed-width date, stored through a value converter, supports all three operations and the
planner uses the index. Asserted end to end on forty thousand rows in
`ContainmentTypeEligibilityProbeTests.ACanonicalDate_IsIndexableForEqualityRangeAndOrderAsync`:

- every value is the same width, so the text sorts in instant order
- equality translates and finds its row
- a two-sided range translates against the provider type, so the parameter is rendered the way the
  row was
- the ordering comes back chronological, which the default trimmed rendering does not
- `EXPLAIN` shows an index scan on the extraction index

That last point is the one that matters: a converter does not put the property beyond the reach of
comparison or ordering, which was the open question the whole plan rested on.

## Canonical stored forms

Chosen so the extraction is both immutable-castable and order-preserving.

| CLR type | Stored as | Index expression |
|----------|-----------|------------------|
| `string` | text | `(data ->> 'X') COLLATE "C"` |
| `Guid` | text, already canonical | `((data ->> 'X')::uuid)` |
| `bool` | boolean | `((data ->> 'X')::bool)` |
| `short`, `int`, `long`, `byte`, enum | number | `((data ->> 'X')::bigint)` |
| `decimal` | number | `((data ->> 'X')::numeric)` |
| `double`, `float` | number | `((data ->> 'X')::float8)` |
| `DateTime` | microseconds since the epoch, as a number | `((data ->> 'X')::bigint)` |
| `DateTimeOffset` | the same, normalized to UTC, with the offset in a sibling key | `((data ->> 'X')::bigint)` |
| `DateOnly` | days since the epoch, as a number | `((data ->> 'X')::bigint)` |
| `TimeOnly` | microseconds since midnight, as a number | `((data ->> 'X')::bigint)` |
| `TimeSpan` | total ticks, as a number | `((data ->> 'X')::bigint)` |
| `char` | its code point, as a number | `((data ->> 'X')::bigint)` |
| `TrackedGuid` | a bare v7 identifier, as text | `((data ->> 'X')::uuid)` |

The collation is pinned to `C` for the one text key that remains, because a text index answers a
range only in its own collation order.

**A number rather than text for the date and time family, measured.** Both forms are correct and both
index, so the choice is a performance one. On thirty-nine thousand rows the text index was 1,982,464
bytes against 901,120 for the numeric one, and the estimated cost of the same range was within a few
percent either way. So the per-query work is a wash and the win is size: an eight-byte key against a
twenty-seven byte one means more of the index stays resident and less of it has to be read.
`ANumericDateIndexesSmallerThanATextOne_IsRecordedAsync` holds the measurement.

Three consequences of the numeric form worth naming, all of them simplifications:

- Equality needs no rendering at all. jsonb compares numbers by value, so the containment document is
  built by passing the number through, and the `to_char`, the trims and the concatenation all go away.
- **The infinities stop being a special case.** `DateTime.MaxValue` in epoch microseconds is
  253402300799999999, which fits an `int64` comfortably, so it is an ordinary number rather than the
  word `infinity`. The whole `isfinite` branch disappears.
- The stored document is no longer human readable for these fields. That is the cost, accepted
  deliberately in favour of the index size.

## Phases

### Phase 0: index the extraction. No format change, no migration.

Add an attribute declaring that a JSON-only field carries an index, and have the generator emit the
expression index alongside the table. Everything except the date and time family gains indexed
equality, ranges and ordering immediately.

The mode is a flag enumeration so the kinds can be combined, and the attribute may be repeated where
that reads better than combining. A range wants btree; `Contains` and `StartsWith` want GIN with
`pg_trgm`; both together is a legitimate ask for a field that is filtered each way.

A perspective can also ask for every field at once, so a read model that is queried every way does
not need a decoration per property:

```csharp
[IndexAllFields]                   // every eligible field gets the default mode
public class ReportRow { /* … */ }
```

```csharp
public class OrderModel {
  [PhysicalField(Indexed = true)]   // real column: everything, costs a column and a hydration path
  public Guid TenantId { get; init; }

  [JsonIndexed]                     // expression index: everything, costs only an index
  public int Rank { get; init; }

  [JsonIndexed(JsonIndexKind.Btree | JsonIndexKind.Trigram)]   // filtered by range and by substring
  public string Title { get; init; } = string.Empty;

}
```

**The containment rewrite must stand down for a field that carries a btree index.** Otherwise
equality is rewritten to `@>`, the planner answers it from GIN, and the btree the consumer paid for
is never used, which recreates the unused-index problem this work started from. A single-column btree
equality probe is also cheaper than GIN containment plus its recheck, so standing down is the better
plan and not merely the safer one.

Three tiers, each with an honest cost, and nothing implicit.

### Phase 1: canonical date and time formats. Needs a migration.

Bring the date and time family into Phase 0's mechanism by owning their stored form.

- A converter per type, emitted by the generator rather than hand-written per property.
- **A tolerant reader, shipped one release ahead of everything else.** This is the part that is easy
  to get subtly wrong, and fusing it into the migration release does get it wrong. The backfill writes
  a form only the new code understands, and the new code has to run against rows the backfill has not
  reached, so a window where both forms exist is unavoidable and something has to read through it.
  That much argues only for tolerance existing. What argues for it existing *first* is rollback: if
  the release that starts writing the canonical form is also the release that learns to read it, then
  rolling back lands on a version that cannot read what its successor wrote.

  So it is plain expand and contract, in three releases:

  | Release | Does | Can the rollback target read? |
  |---------|------|-------------------------------|
  | 1 | Reads both forms. Writes nothing differently, changes no behavior | not applicable |
  | 2 | Writes the canonical form, backfills, then creates the index | yes, release 1 reads both |
  | 3 | Drops the tolerance, optionally | yes, everything is canonical |

  Release one is a no-op that can be deployed and forgotten; it is the thing that makes release two
  reversible.

  Worth keeping afterwards regardless, for two reasons that cost almost nothing. A backup restored
  from before the migration, a shard added late or a batch that failed leaves rows in the old form,
  and tolerance makes those degraded rather than broken. And with the numeric form the discriminator
  is the JSON type itself, a number being canonical and a string being legacy, so it is one type test
  rather than a parse attempt.

  What stops it hiding an incomplete backfill is the index: PostgreSQL refuses to build one while any
  row is still a string, so a migration that did not finish fails loudly at exactly the step that
  depends on it.
- **A tolerant filter while migrating.** Containment emits
  `data @> {canonical} OR data @> {legacy}`; both arms stay indexed, measured as a `BitmapOr` over two
  index scans. Behind a flag defaulting on, removable a release later. Without this a filter silently
  returns only the migrated rows, which is the failure mode to avoid above all others.
- **A backfill migration, which is required rather than optional.** A tolerant reader makes
  materializing a row work whichever format it holds, and that is worth having, but it settles
  nothing about what the database can do while the rows are mixed. Measured on a table holding one
  row of each form: an equality against the new form still finds its row, so nothing looks broken; a
  range over the column raises an error rather than skipping the odd row; and the index cannot be
  created at all, because building it evaluates the expression for every row. Since the index is the
  whole point of the change, a reader that tolerates both formats does not remove the need to rewrite
  them. `AMixedFormatColumnCannotBeIndexedOrRangeQueriedAsync` holds that measurement.

  The backfill itself is small: the format is reproducible in SQL, so it is an in-place `jsonb_set`
  over the data column in batches, with no replay, no rebuild and no .NET involvement.

  This is also a second and independent argument for the numeric form. A mixed column of numbers
  fails loudly, refusing both the index and the range; a mixed column of fixed-width text builds an
  index happily and answers ranges with the wrong rows, because the two renderings do not sort
  consistently against each other. Writing the canonical value to a second key instead of replacing
  the first has the same defect: the index builds, and every range silently skips whatever has not
  been migrated yet.
- **The expression index is created after the backfill, not with it**, and usefully this enforces
  itself: with the numeric form PostgreSQL refuses to build it while any row is still in the old
  rendering, so the optimization cannot be shipped ahead of the data that supports it.

Note that a row only rewrites itself when its stream sees a new event, so cold streams never migrate
on their own. The backfill is required, not optional; the tolerant reader is the safety net around it
rather than a substitute for it.

### Phase 2: value objects keep their index, and TrackedGuid is just a v7 identifier.

**Attempted and reverted once; read this before trying again.** Resolving the overload from the
converter's provider type does make the rewrite fire for a value object, and the member side
translates correctly. The value side does not: the comparison is written against the value object, so
reaching the identifier's overload needs a conversion on that operand too, and the converted constant
arrives at the SQL tree with no type mapping assigned. Entity Framework then refuses the query
outright. The obvious workarounds are all worse than the problem: evaluating the conversion at
rewrite time needs reflection this assembly avoids, and supplying a mapping from the emission means
guessing one for a CLR type the emission has no mapping source for.

What would actually solve it is the seam noted below rather than another attempt at this one: a
translation post-processor reshapes the comparison Entity Framework has already translated, converter
applied to both sides, so no operand needs converting by hand and no mapping has to be invented. The
test that records the current behavior is
`RemainingCandidates_AreRecordedAsync`, whose `Tracked: extraction` assertion is written to say it is
expected to fail when this lands.

A converter down to an eligible type stores exactly what the bare type stores. `TrackedGuid` lands as
a plain guid string, byte for byte what a `Guid` lands as, yet the filter currently falls back to
extraction because the converter guard is a blanket one. Resolving the overload from the converter's
provider type rather than the model type fixes it for every wrapper of this shape.

`TrackedGuid` is then treated as what it is, a time-ordered identifier, with the converter registered
by the framework rather than written out per property. Its creation metadata is not persisted and was
never meant to be: it is authoritative only at creation, and a value read back from a document
reports itself as untracked anyway.

That also makes it fully orderable and range-scannable, which is worth more than it sounds. A version
7 identifier's text ordering, its `uuid` byte ordering and its creation ordering all agree, asserted
in `ATimeOrderedIdentifier_SortsTheSameAsTextAndAsBytesAsync`, so cursor paging over a document-held
identifier is answerable from an index.

### Phase 3: advise the cheap fix.

WHIZ302 currently advises promoting a field to a column, which is the heaviest of the three tiers. It
already knows the filter shape, so it can name the fields that want `[JsonIndexed]` and reserve the
promotion advice for cases that need a constraint or a foreign key.

## Reshaping the translated SQL instead of the expression tree

The rewrite currently happens on the LINQ tree: a comparison is replaced with a marker call before
Entity Framework translates anything, and the marker's registered translation builds the containment
test. The alternative is to let Entity Framework translate the query as it normally would and then
reshape the result, turning `CAST(data ->> 'K' AS t) = <value>` into
`data @> jsonb_build_object('K', <value>)`.

### What it entails

Subclass `RelationalQueryTranslationPostprocessor`, override `Process`, call the base implementation
first and then run a visitor over what it returns. The ordering is the whole point: the base pass is
where type mappings are assigned, so afterwards every node carries one. Register it by replacing
`IQueryTranslationPostprocessorFactory` from `UseWhizbangJsonbContainment`.

The visitor reshapes a comparison only in a predicate position, which at this level is a structural
fact rather than an inference: `SelectExpression.Predicate`, a join's predicate, and a `Having`
clause are predicates, and a projection is not.

### Why it is worth considering

**It makes value converters correct for free, which is the thing the current design cannot do.** By
the time the translated tree exists, Entity Framework has applied the converter to *both* sides of the
comparison. An identifier held in a value object arrives as the identifier; a number configured to
store as text arrives as that text. Reshaping either produces a document built in exactly the stored
form, so there is nothing to convert by hand and no mapping to invent. That is the whole of the
reverted value-object phase, and it also removes the class of bug the converter guard exists to
prevent rather than merely guarding against it.

**It removes the type dispatch.** No overload per type, no `OverloadFor`, no eligibility list, no
agreement required between the rewriter and the emission, and no planted conversion to see through.
Whatever Entity Framework produced is reshaped.

**It narrows what has to agree.** Today the rewriter and the emission must reach the same conclusion
about a stored form, and the analyzer must reach it a third time. A reshape has one place.

### What it costs, and one correction

**It does not solve the date problem, and I said earlier that it did.** That was wrong. Entity
Framework renders a date parameter as a timestamp, so a reshaped date comparison would build
`jsonb_build_object('When', <timestamptz>)`, whose text is PostgreSQL's ISO form with an explicit
offset rather than the trailing `Z` the serializer writes. Stored-form agreement is a separate
problem from type dispatch, and only the canonical format fixes it. The two changes are
complementary, not substitutes.

**It would lose a capability unless it stays a hybrid.** The one place this framework does more than
Entity Framework is `Equals(value, StringComparison.Ordinal)`, which Entity Framework refuses to
translate at all. Intercepting it depends on seeing the LINQ tree; after translation it has already
thrown. So the expression-tree rewriter has to stay for that, and the change is an addition rather
than a replacement.

**It goes deeper into internals.** `PgUnknownBinaryExpression` is already an internal seam. Replacing
a translation postprocessor and reconstructing a `SelectExpression` is more of that surface, which
makes the version pin in `ProviderCapabilities` matter more rather than less.

**It replaces a mechanism that currently has 1040 green cases.** That is the strongest argument for
caution and also, as below, the strongest tool for managing it.

### Reducing the risk

**Two spike gates, and abandon if either fails.** First, that a visitor running after the base pass
sees type mappings assigned on every node, which is the claim the whole idea rests on. Second, that a
`SelectExpression` predicate can actually be replaced from that position: the type is close to
immutable by design and the update surface may not permit it. Neither is worth guessing about, and
failing the second means the idea is dead rather than merely harder.

**Run both mechanisms against the same matrix.** The 1040 cases assert where a filter landed by
reading compiled SQL, which makes them indifferent to how it got there. Parameterizing them over the
mechanism doubles the suite and turns any divergence into a named failing case rather than a
production surprise. This is the single most valuable thing available and it costs almost nothing,
because the asset already exists.

**Keep the execution tests per mechanism.** Asserting on generated SQL cannot distinguish a valid
statement from an invalid one, which this work has already been caught by once. The on-versus-off row
comparisons have to run against the new path too.

**Three-state configuration rather than two.** Off, expression tree, translated tree, defaulting to
the current mechanism until the new one has been green in continuous integration for a release. The
existing switch already proves the plumbing for a default-on flag.

**Sequence it before the canonical formats, not after.** Phase 1 adds a converter to every date
property, and converters are exactly what the current mechanism handles badly and this one handles for
free. Doing Phase 1 first means teaching the current emission about canonical date converters and
then deleting that work; doing this first means Phase 1's converters simply work, and the date
rendering it still needs is the only part left to build.

## The hybrid, laid out

The guiding rule is that each stage owns only the decisions it is uniquely able to make, and the
shapes both stages need are stated once. Every problem this work has hit came from two places having
to agree about the same fact, so the layout is chosen to leave as few of those as possible.

### What only the expression tree can do

See a shape Entity Framework refuses to translate. `Equals(value, StringComparison.Ordinal)` is the
whole of that list today: Entity Framework throws on it, so after translation there is nothing left to
inspect.

That reframes what the tree stage is for. Today it does two unrelated jobs, working around that
refusal and building containment. Split them and the first becomes tiny and permanent:

**The tree stage normalizes an untranslatable shape into a translatable one, and nothing else.**
`Equals(value, StringComparison.Ordinal)` becomes `==`. It does not build containment, does not know
which types are eligible, and does not know containment exists. The downstream stage then sees an
ordinary equality and reshapes it like any other, so the capability is preserved with no special case
anywhere else.

### What only the translated tree can do

See the value converter already applied to both sides. See the cast Entity Framework actually chose.
Know structurally, rather than by inferring from operator names, whether a comparison sits in a
predicate. And have a type mapping on every node.

**The translated stage reshapes an equality into a containment test, type-agnostically.**

### Where the files go

```text
QueryTranslation/
  Containment/
    ContainmentMode.cs                 off | expressionTree | translatedTree
    JsonbContainmentSwitch.cs          reads the mode (moves here)
    JsonbContainmentSql.cs             the operator and the document shape, shared by both modes
    ExpressionTree/
      JsonbContainment.cs              markers and their translation; used by this mode only
      JsonbContainmentRewriter.cs      unchanged behavior
    TranslatedTree/
      ContainmentPostprocessorFactory.cs
      ContainmentPostprocessor.cs      Process: base first, then visit
      ContainmentSqlRewriter.cs        the visitor
  Compatibility/
    OrdinalEqualsRewriter.cs           runs in both modes; the tree rewrite that survives the flip
```

`JsonbContainmentSql` is the important one. It owns the containment operator and the nested
`jsonb_build_object` shape, and both modes call it, so the document being built is defined in one
place even while two mechanisms exist. Without that, the two modes are two chances to build a
different document and the matrix would be the only thing standing between them and a wrong answer.

The folder split is deliberate rather than tidy: it should be obvious at a glance which mode a file
belongs to, so that a change meant for one is not made to the other.

### What cannot be shared, and what covers it

Four stand-down rules are needed by both modes: a comparison against null, anything under a negation,
a field that carries its own btree, and predicate position. They are expressed against different
trees, so the code cannot literally be shared; only the rules can be. That is the one real
duplication in this design, and it is covered by running the matrix over both modes rather than by a
comment asking people to remember.

### Dates, and the regression to accept on purpose

A date works today through a SQL-side rendering of the instant. The translated stage has no per-type
code by design, and giving it some for dates would mean writing exactly the code the canonical format
is going to delete.

So under `translatedTree` a date falls back to the extraction form: correct rows, no index, exactly as
it behaved before this branch. Not a wrong answer, and visible rather than silent, because the matrix
expects `Containment` for a date under `expressionTree` and `Extraction` under `translatedTree`. The
canonical format removes the difference, at which point a date needs nothing special from either
stage.

This is the reason the default stays `expressionTree` until Phase 1 lands. Nobody loses an index in
the meantime, and the new mode is exercised in continuous integration the whole way.

### What gets deleted when the default flips

The overload table and `OverloadFor`, `StoredFormIsNatural`, the planted-conversion unwrap, the
floating-point cast, the date rendering, and the type-set pin that exists to keep the eligible list
honest. `WHIZ302`'s type check collapses to "any type", since eligibility stops being a concept.
Migration 152 stays: set membership still needs the helper, and reshaping `= ANY(...)` in the
translated stage is more general than the current exact-type overload lookup, not less.

None of that is deleted while both modes ship. The clean-up is a separate step after the flip, so a
rollback never depends on code that has already been removed.

## Decisions taken

1. **Opt-in per field, with a perspective-level option to index everything.** A field carries
   `[JsonIndexed]`; a read model that is queried every way carries `[IndexAllFields]` instead of a
   decoration per property. The analyzer still names the fields that want it, so the opt-in is guided
   rather than guessed.
2. **`DateTimeOffset` normalizes to UTC, and the offset is kept in a sibling key.** Equality and
   ordering then mean what `==` and a chronological sort mean, both are indexable, and the offset the
   row was written with is still recoverable for display. The offset is derived from the normalized
   instant at query time rather than filtered on.
3. **A number, not text.** Measured: an eight-byte key indexes at less than half the size of a
   twenty-seven byte one for the same rows and the same plan. Readability of the stored document is
   the deliberate cost.
4. **One attribute, a flag enumeration for the mode, repeatable.** Btree by default, `pg_trgm` for
   substring matching, combinable where a field is filtered both ways.
5. **A null character is refused at the mapping, not at the driver.** See below.

## What is already done

- Equality via containment for string, Guid, bool, the integer family, decimal, double, float, enums
  including flag enums, and `DateTime`.
- `DateTimeOffset` deliberately excluded, with the reason measured rather than assumed.
- 1040 compiled-SQL cases pinning where every type and operator lands, including a fourth destination
  for the shapes Entity Framework refuses outright.
- The stored format of every date precision, and both infinities, locked by assertion.
- Ordering proven chronological despite the stored text not being, because ordering sorts the parsed
  instant and the rewrite never reaches an ordering key.

## Findings worth their own issues

- **`TimeOnly` is written with seven fractional digits while `DateTime` is written with six.** The
  document writer does not truncate uniformly, which is why the `DateTime` precision is locked by a
  test rather than trusted. The numeric stored form removes the discrepancy along with the question.

## The null character, and where it is refused

A `char` left at its default is the null character, and jsonb has no representation for one. The save
fails with a driver error naming neither the property nor the model, and a string carrying an embedded
null fails the same way: `22021` when it arrives as a parameter, `22P05` when it arrives as an escape
in a document. `ANullCharacterCannotReachTheDocumentAsync` pins both.

Two fixes, and they are for two different problems:

- **`char` stores as its code point**, a number, which is where the canonical-format table already
  puts it. A default `char` is then the number zero, which is an ordinary value, so the common case
  stops failing rather than being reported better.
- **A string carrying a null is refused by the framework**, at the point of writing the document,
  with a message naming the perspective and the property. It cannot be fixed by a format choice
  because the value itself has no representation, so the only improvement available is to fail where
  a developer can act on it instead of at the driver. An analyzer cannot catch it, since the value is
  only known at run time.
