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

  [Indexed]                     // expression index: everything, costs only an index
  public int Rank { get; init; }

  [Indexed(IndexKind.Btree | IndexKind.Trigram)]   // filtered by range and by substring
  public string Title { get; init; } = string.Empty;

}
```

**The containment rewrite must stand down for a field that carries a btree index.** Otherwise
equality is rewritten to `@>`, the planner answers it from GIN, and the btree the consumer paid for
is never used, which recreates the unused-index problem this work started from. A single-column btree
equality probe is also cheaper than GIN containment plus its recheck, so standing down is the better
plan and not merely the safer one.

Three tiers, each with an honest cost, and nothing implicit.

### Phase 1: canonical date and time formats.

Bring the date and time family into Phase 0's mechanism by owning their stored form: microseconds
since the epoch as a number, which the measurements chose over fixed-width text.

**The rows are rewritten before anything queries them, which removes most of the machinery an
in-place format change would otherwise need.** Migrations run ahead of the application serving
traffic, so there is no window in which a query can observe a mixed column: the rows are rewritten,
then the index is created, then queries run. That deletes the tolerant reader, the filter that had to
match both forms while they coexisted, and the three-release sequence those implied. This is a pre-1.0
library whose consumers migrate deliberately, which is exactly the situation that sequence exists to
avoid needing.

**The blocker found on the way in, which was much larger than Phase 1.** A model declared as a
`record` was classified as needing opaque storage, every time. The compiler generates a protected
`EqualityContract` of type `System.Type`, `System.Type` is an abstract class, and the detector read it
as a declared property, so the first member of every record answered the question before anything the
author wrote was reached.

That decides far more than which snippet is emitted. An opaquely stored model is one jsonb value, so
nothing inside it is a mapped property: no extraction to index, nothing for the rewrite to recognize,
nowhere to attach a conversion. Every record model sat outside all three, and the sample models in
this repository are records. Detection now considers only public properties, the rule both the mapped
path and the serializer already follow. The Postgres suite is unchanged at 5394 green, which is the
measurement that the reclassification breaks nothing.

Three diagnostics were made to agree with it, since they now describe a fork that really exists:
WHIZ304 reports an index declared on a model whose storage puts it out of reach, the generator skips
emitting that index rather than maintaining one nothing can scan, and WHIZ302 stops offering
`[Indexed]` on such a model, which would have sent an author into WHIZ304 for taking the advice.

A genuinely polymorphic model is deliberately left in the serializer's own temporal form. It can carry
no index either way, so converting it would be a stored-format change that buys nothing.

What remains is three things.

- **A converter per type, emitted by the generator** rather than written out per property, so the
  stored form is a decision the framework makes once rather than one every model repeats. *Done:
  `CanonicalTemporalDiscovery` emits them into the mapped configuration, so a fresh database writes
  the canonical form from its first row and never migrates.*
- **A backfill emitted per perspective, not a hand-written migration.** Which keys hold dates is
  per-model knowledge, so it belongs where the perspective's other schema is generated. It rewrites
  only what is not already converted, which makes re-running it a no-op and a restored backup
  self-correcting.
- **The index, in the same script, after the rewrite.** The ordering is not merely convention: with
  the numeric form PostgreSQL refuses to build the index while any row is still a string, so a
  backfill that did not finish fails at the next step rather than leaving a silently useless index.
  *Done, for both schema paths, which have to agree because the per-perspective entries are
  hash-tracked against the concatenated init SQL.*

**Two things the tests found that the design did not anticipate.** PostgreSQL's cast to `time`
**rounds** a seventh fractional digit where the writer truncates it, so a time of day would have been
rewritten one microsecond away from what the writer produces: a difference no query would surface and
every comparison would be wrong about. The extra digits are dropped from the text before the cast.
And a duration cannot be parsed by PostgreSQL at all, because the rendering separates days from the
clock with a period where an interval wants a space and carries a digit an interval could not hold;
its components are matched and the ticks computed, with the sign multiplied through rather than
applied to the day count, which would be right only for a whole number of days.

**The date rendering is not deleted yet, and that is deliberate.** It reads like Phase 1 work and it
is not: see *What gets deleted when the default flips*. While both modes ship, a rollback must not
depend on code that has already been removed. The rendering is now unreachable from any generated
model, since every mapped model gets a conversion, so what it still covers is a hand-written context.

The measurement behind requiring the rewrite at all is still worth keeping, because it is what rules
out the cheaper options. On a column holding one row of each form, an equality against the new form
still finds its row so nothing looks broken; a range raises an error rather than skipping the odd
row; and the index cannot be built, because building it evaluates the expression for every row.
`AMixedFormatColumnCannotBeIndexedOrRangeQueriedAsync` holds it.

That measurement also argues again for numbers over fixed-width text, and against writing the
canonical value to a second key while leaving the first. Both of those alternatives build an index
happily over a half-migrated column and answer ranges with the wrong rows.

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
already knows the filter shape, so it can name the fields that want `[Indexed]` and reserve the
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
   `[Indexed]`; a read model that is queried every way carries `[IndexAllFields]` instead of a
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
- **Every `record` model was classified as needing opaque storage** and so was excluded from indexing,
  the containment rewrite and value conversion alike, on a compiler-generated member no serializer
  writes. Fixed here rather than filed, because Phase 1 could not proceed past it, but worth recording
  as its own finding: it had been noted in a test comment as a trap to write around rather than as a
  defect to fix, and writing around it is how it survived.

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
