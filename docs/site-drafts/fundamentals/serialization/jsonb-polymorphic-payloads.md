---
title: 'Polymorphic DTOs inside stored events (Postgres jsonb)'
pageType: concept
description: >-
  How Whizbang stores and reads polymorphic DTOs nested inside events on Postgres,
  why jsonb key reordering interacts with System.Text.Json's positional type
  discriminator, and how the framework handles it for you.
version: 1.0.0
category: Serialization
tags:
  - serialization
  - polymorphism
  - postgres
  - jsonb
  - event-store
---

> **Release note.** This page ships with the fix that makes nested polymorphic DTOs round-trip
> reliably through Postgres `jsonb`. If you declared custom discriminators to work around read
> failures, see *App guidance* below — you can remove those workarounds.

## The short version

You can nest a polymorphic DTO (an `abstract record`/interface with `[JsonPolymorphic]` +
`[JsonDerivedType]`) inside an event, and Whizbang round-trips it correctly through a Postgres
`jsonb` column. You do **not** need to configure anything — the framework sets the one option that
makes this work. This page explains why the option is needed so the behavior isn't surprising.

## Why it needs handling at all

Two independent, documented behaviors collide:

1. **System.Text.Json writes the type discriminator first.** A polymorphic value serializes as
   `{"$type":"ShapeObjectDto","A":680,...}` — `$type` leads the object. On read, STJ requires the
   discriminator to appear **first**, unless
   [`JsonSerializerOptions.AllowOutOfOrderMetadataProperties`](https://learn.microsoft.com/dotnet/api/system.text.json.jsonserializeroptions.allowoutofordermetadataproperties)
   is enabled.
2. **Postgres `jsonb` does not preserve object key order.** It normalizes keys by *length, then
   bytewise*. `$type` is 5 bytes, so a stored `{"$type":"…","A":680,…}` comes back as
   `{"A":680,…,"$type":"…"}` whenever the object has a **direct** sibling key shorter than `$type`
   (≤4 characters). The discriminator is no longer first.

Read back, STJ then throws:

```
System.NotSupportedException: The JSON payload for polymorphic interface or abstract type
'…PictureObjectDto' must specify a type discriminator. Path: $.Object
```

**The fix:** `JsonContextRegistry.CreateCombinedOptions` — the single options factory every read path
flows through (EF Core and Dapper event stores, perspective persistence, snapshot reads) — enables
`AllowOutOfOrderMetadataProperties`. So the discriminator is honored wherever it lands in the object.

## When it triggers (and when it doesn't)

The trigger is precise: a polymorphically-dispatched object has a **direct** sibling property whose
JSON name is **≤4 characters** (a 5-char name still sorts after `$type` for any real identifier).

| Shape | Reordered form | Result before the fix |
|---|---|---|
| Leaf has a 1–4 char direct prop (`A`, `Id`, `Code`) | `A`, …, `$type`, … | **Breaks** (discriminator not first) |
| Leaf's direct props are all ≥5 chars (`IsRequired`, `Shape`) | `$type`, … | Fine (discriminator stays first) |
| Short prop is nested inside a *non-polymorphic* sub-object | `$type` stays first at the dispatch level | Fine |
| Top-level event (resolved by `event_type` column, not `$type`) | n/a | Fine |

This is why two apps using the identical pattern can behave differently: one whose polymorphic leaves
have short property names hits it; one whose leaves happen to have all-long names does not — the
latter only by luck. The fix removes the dependency on property-name length entirely.

## App guidance

- **Let Whizbang manage the wire format.** For DTOs stored inside events, the generator emits the
  discriminator as `$type` + the CLR simple type name. Your `[JsonDerivedType]` attributes are used to
  *discover* the derived types; a custom `TypeDiscriminatorPropertyName` (e.g. `"Kind"`) or custom
  discriminator *values* are not honored on the persistence path. Declare the derived types, but don't
  fight the `$type` convention.
- **Renaming a derived DTO is a stored-data event.** The discriminator is the CLR simple name, so
  renaming a concrete type changes what's written. Treat it like any other stored-schema change.
- **Nothing to configure for the reorder.** `AllowOutOfOrderMetadataProperties` is set for you.

## Verified by

These behaviors are locked by regression tests (shown on this page via the code-tests map):

- `JsonbPolymorphicOrderingTests.NestedPolymorphic_ShortKey_JsonbReordered_RoundTripsThroughCombinedOptionsAsync`
  — the bug reproduced and fixed through the real registry + a jsonb key-order simulator.
- `JsonbPolymorphicOrderingTests.CreateCombinedOptions_EnablesOutOfOrderMetadata_DefaultProfileAsync`
  and `…_PersistenceProfileAsync` — the option is set on both serialization profiles.
- `JsonbPolymorphicOrderingTests.Simulator_MatchesRealPostgresJsonbOrderingAsync` /
  `…_FiveCharSibling_KeepsDiscriminatorFirstAsync` — the length-then-bytewise ordering rule and the
  ≤4-char trigger boundary, validated against real Postgres output.
- `JsonbPolymorphicRoundTripIntegrationTests.ReadAsync_EventWithNestedPolymorphicShortKeyDto_RoundTripsThroughRealJsonbAsync`
  — end-to-end through a real Postgres `jsonb` column and the real generated polymorphic factory.

## Related

- Deserialize failures on the drain path are now logged instead of silently dropped
  (`DeserializeStreamEventsTests.DeserializeStreamEvents_WhenEventDataIsCorrupt_LogsWarningAndKeepsGoodEventsAsync`).
