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
- **A tolerant reader, permanently.** `ConvertFromProvider` accepts the canonical form and the legacy
  one. Reading never breaks, which keeps rollback safe and stragglers harmless.
- **A tolerant filter while migrating.** Containment emits
  `data @> {canonical} OR data @> {legacy}`; both arms stay indexed, measured as a `BitmapOr` over two
  index scans. Behind a flag defaulting on, removable a release later. Without this a filter silently
  returns only the migrated rows, which is the failure mode to avoid above all others.
- **A backfill migration.** The format is reproducible in SQL, so it is an in-place `jsonb_set` over
  the data column in batches. No replay, no rebuild, no .NET involvement.
- **The expression index is created after the backfill, not with it.** A text index over mixed formats
  sorts wrongly, because the two formats do not sort consistently against each other.

Note that a row only rewrites itself when its stream sees a new event, so cold streams never migrate
on their own. The backfill is required, not optional; the tolerant reader is the safety net around it
rather than a substitute for it.

### Phase 2: value objects keep their index, and TrackedGuid is just a v7 identifier.

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
