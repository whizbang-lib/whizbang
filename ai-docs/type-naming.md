# Type naming: one helper per form, on each side

Type-name strings are keys all over the framework: `event_type`, `aggregate_type`, `clr_type_name`,
perspective names, registry JSON, association targets, digest columns, routing tables. One side
writes the string, the other side looks it up, and the two sides are different programs: a source
generator at compile time and the runtime at startup. There is exactly one correct rendering per
form, and the shared helpers exist on both sides. A site that renders its own has produced a
production incident more than once (issue #697 is the latest: the registry key for a nested
perspective model was written as `Outer.Model` and looked up as `Outer+Model`, so row retention
was silently un-enrolled for every nested model).

**Rule: a type name that is a key comes from the shared helper, never from a local rendering.**
This applies to generator code and to regular C# alike.

## The forms and their helpers

| Form | Example | Runtime (`Whizbang.Core`) | Generator (`Whizbang.Generators.Shared`) |
|------|---------|---------------------------|------------------------------------------|
| CLR form (no assembly, `+` for nested, arity on generics) | `MyApp.Outer+Model`, `MyApp.Generic`1` | `TypeNameFormatter.FormatClrTypeName(Type)` | `TypeNameUtilities.BuildClrTypeName(ITypeSymbol)` |
| Wire form (CLR form plus the simple assembly name) | `MyApp.Outer+Model, MyApp` | `TypeNameFormatter.Format(Type)` | `TypeNameUtilities.FormatTypeNameForRuntime(ITypeSymbol)` |
| Simple name (display only) | `Model` | `TypeNameFormatter.GetSimpleName(string)` | `TypeNameUtilities.GetSimpleName(...)` |
| Fully qualified for generated source | `global::MyApp.Outer.Model` | n/a | `ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)` (code generation only, never a key) |

Which columns hold which form:

- CLR form: `wh_perspective_registry.clr_type_name`, `wh_message_type_registry.clr_type_name`,
  `wh_event_store.aggregate_type`, perspective names, `PerspectiveRetentionDeclaration.ClrTypeName`,
  `PerspectiveRegistrationInfo.ClrTypeName`.
- Wire form: `wh_event_store.event_type`, `wh_outbox.message_type`, `wh_inbox.message_type`,
  envelope type names on the wire.

The two helpers on each row are documented mirrors of each other, and
`tests/Whizbang.Generators.Tests/TypeNameMirrorContractTests.cs` pins them to each other on one
corpus (top level, nested, doubly nested, generic, generic nested in generic, global namespace):
the corpus is compiled once, so the Roslyn symbol and the loaded CLR type come from the same
declaration. Extend that corpus when a helper grows a shape.

## Comparing a persisted name with a registered one

Never compare with `==`. A producer may have written the decorated assembly-qualified form
(`Version=`, `Culture=`, `PublicKeyToken=`), an older build may have written a different assembly
name, and a nested type may appear with `.` from a display string. Go through
`EventTypeMatchingHelper`:

- `EventTypeMatchingHelper.BuildTypeLookup(candidateTypes)` builds the lookup once, keyed by every
  form a producer may have written.
- `EventTypeMatchingHelper.TryResolveType(lookup, storedName, out type)` resolves a stored name,
  raw first and normalized second.
- `EventTypeMatchingHelper.NormalizeTypeName(name)` strips the version decoration when a bare
  comparison is unavoidable.

The polymorphic event-store reads and the resurrection-on-wake probe (issue #696) use exactly this.

## What a bypass looks like

Any of these is a bug waiting for a nested or generic type, even when it produces the same string
today:

- `type.FullName` or `type.AssemblyQualifiedName` assigned to anything named `*ClrTypeName*`,
  `*TypeName*`, `*EventType*`, or bound to a SQL parameter for one of the columns above.
- `symbol.ToDisplayString(...)` with `global::` stripped, used as a key.
- A private method that walks `ContainingType` and joins with `+` (there was one in
  `MessageJsonContextGenerator`; it now delegates to the helper).
- String interpolation that composes a name from parts: `$"{ns}.{name}"`, `x + "+" + y`,
  `$"{name}, {assembly}"`.
- `.Replace("global::", "")`, `.Split(',')`, `IndexOf('+')` applied to a name that came from a helper.

Display-only sites (tracing tags, exception messages, log lines) may use `Type.FullName` or
`Type.Name`; keep them out of anything persisted or compared.

## Enforcement

- `BannedSymbols.txt` (root, wired by `Directory.Build.props` for production projects) is the
  build-time layer for raw sources that must never appear outside the helper files. Add the
  banned symbol with a message that names the helper to use; the helper file carries the one
  `#pragma warning disable RS0030`.
- The mirror-contract test above fails the build of the generator tests when the two sides
  drift.
- Issue #698 tracks the remaining layers: typed keys (`ClrTypeName` and `WireTypeName` value
  objects constructible only through `TypeNameFormatter`) and a WHIZ naming analyzer for the
  residue that neither banned APIs nor typed keys catch (string composition, `==` on persisted
  names, `*TypeName*` members assigned from anything but a helper result).

## Checklist when you touch a naming site

1. Is the string a key (persisted, compared, or routed on)? Then it comes from the helper for that
   form, on that side.
2. Is it compared against a persisted value? Then it goes through `EventTypeMatchingHelper`.
3. Did you add a shape (a new form, a new column)? Add it to the table above, to both helpers,
   and to the mirror-contract corpus.
4. Did you write a test? Use a nested model or event in it; a top-level type hides every one of
   these bugs.
