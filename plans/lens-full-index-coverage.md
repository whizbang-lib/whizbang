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
| `DateTime` | `yyyy-MM-ddTHH:mm:ss.ffffffZ`, fixed width, UTC | `(data ->> 'X') COLLATE "C"` |
| `DateTimeOffset` | the same, normalized to UTC | `(data ->> 'X') COLLATE "C"` |
| `DateOnly` | `yyyy-MM-dd`, already fixed width | `(data ->> 'X') COLLATE "C"` |
| `TimeOnly` | `HH:mm:ss.fffffff`, already fixed width | `(data ->> 'X') COLLATE "C"` |
| `TimeSpan` | total ticks, as a number | `((data ->> 'X')::numeric)` |
| a value object | its underlying type's form | its underlying type's expression |

The collation is pinned to `C` because a text index answers a range only in its own collation order.
Byte ordering over a fixed-width ASCII rendering is the chronological order; a locale-aware collation
may weight punctuation differently and the guarantee is lost.

Two notes on what the format change buys where. For `DateTime` it is not needed for equality, which
already works by rendering the token in SQL; it is needed for the range and the ordering. For
`TimeOnly`, `TimeSpan` and `DateTimeOffset` it is needed for all three, because the token cannot be
constructed from the default form at all.

## Phases

### Phase 0: index the extraction. No format change, no migration.

Add an attribute declaring that a JSON-only field carries an index, and have the generator emit the
expression index alongside the table. Everything except the date and time family gains indexed
equality, ranges and ordering immediately.

```csharp
public class OrderModel {
  [PhysicalField(Indexed = true)]   // real column: everything, costs a column and a hydration path
  public Guid TenantId { get; init; }

  [JsonIndexed]                     // expression index: everything, costs only an index
  public int Rank { get; init; }

  public string Title { get; init; } = string.Empty;   // nothing declared: GIN equality, the rest scans
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

### Phase 2: value objects keep their index.

A converter down to an eligible type stores exactly what the bare type stores. `TrackedGuid` lands as
a plain guid string, byte for byte what a `Guid` lands as, yet the filter currently falls back to
extraction because the converter guard is a blanket one. Resolving the overload from the converter's
provider type rather than the model type fixes it for every wrapper of this shape.

### Phase 3: advise the cheap fix.

WHIZ302 currently advises promoting a field to a column, which is the heaviest of the three tiers. It
already knows the filter shape, so it can name the fields that want `[JsonIndexed]` and reserve the
promotion advice for cases that need a constraint or a foreign key.

## Decisions needed

1. **Opt-in or index everything?** Indexing every field is write amplification and disk for fields
   nobody filters. Recommended: opt-in, with the analyzer naming the fields that want it, since it
   already sees how each field is queried.
2. **`DateTimeOffset` read-back.** Normalizing to UTC makes equality mean what `==` means and makes
   the field indexable, at the cost of the offset the row was written with. Preserving it needs a
   sibling key. Recommended: normalize, and keep the offset in a sibling key only where a model asks.
3. **Date as text or as a number?** Fixed-width text keeps the document readable and is verified
   working. Epoch microseconds need no collation care and no infinity special case, but make the
   document opaque. Recommended: text.
4. **One attribute or several?** A range wants btree; `Contains` and `StartsWith` want GIN with
   `pg_trgm`. Recommended: one attribute with a mode, defaulting to btree.

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

- **A `char` property left at its default cannot be persisted at all.** The default is the null
  character, Entity Framework writes it as a backslash-u-0000 escape, and jsonb rejects that with `22P05`. Any model
  with an unset `char` fails on save. Worth an analyzer diagnostic or a mapping refusal rather than a
  runtime error.
- **`TrackedGuid` cannot be mapped as a complex type.** It exposes its value alongside creation
  metadata and keeps its constructor private, so the model fails to build rather than failing to
  filter. A converter is the only mapping, which Phase 2 then makes indexable.
- **`TimeOnly` is written with seven fractional digits while `DateTime` is written with six.** The
  document writer does not truncate uniformly, which is why the `DateTime` precision is locked by a
  test rather than trusted.
