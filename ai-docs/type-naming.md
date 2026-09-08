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
| Display text (logs, traces, exception messages, metric tags; never a key) | `MyApp.Outer+Model` | `TypeNameFormatter.DisplayName(Type)` | `TypeNameUtilities.Display(ISymbol)` (`MyApp.Outer.Model`; `IsNamed(symbol, "...")` to compare against a known display name) |
| Versioned assembly-qualified form (the envelope-type wire header and a few storage paths still carry it) | `MyApp.Outer+Model, MyApp, Version=1.0.0.0, ...` | `TypeNameFormatter.AssemblyQualifiedName(Type)` / `AssemblyQualifiedNameOrNull` | n/a |
| Envelope type name | `Whizbang.Core.Observability.MessageEnvelope`1[[<wire or versioned form>]], Whizbang.Core` | `EnvelopeTypeNameHelper.Format(inner)` / `ExtractInnerTypeName(envelopeType)` | n/a |
| Fully qualified for generated source | `global::MyApp.Outer.Model` | n/a | `TypeNameUtilities.FullyQualified(ISymbol)` (`FullyQualifiedWithNullability`, `MinimallyQualified`; code generation only, never a key) |
| Namespace for a generated `namespace X;` line | `MyApp.Outer`, empty for the global namespace | n/a | `TypeNameUtilities.NamespaceName(INamespaceSymbol)` |

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

## Enforcement (issue #698)

- **Banned raw sources, runtime side.** `BannedSymbols.txt` (root, wired by `Directory.Build.props`
  for production projects) bans `Type.FullName`, `Type.AssemblyQualifiedName` and
  `Assembly.FullName`. The only files that may read them are `TypeNameFormatter.cs` and
  `EventTypeMatchingHelper.cs`, each wrapped in one `#pragma warning disable RS0030`. Every other
  site names the form it wants through the table above. A `typeof(X).FullName` in an attribute
  argument or a `const` cannot call a helper: wrap that one line in the pragma with a one-line
  reason so the audit stays visible.
- **Banned raw sources, generator side.** `BannedSymbols.Generators.txt` (wired for every project
  whose name contains `Generators`) bans `ISymbol.ToDisplayString`, `ToDisplayParts` and
  `MetadataName`. The only file that may call them is `TypeNameUtilities.cs`. A local
  `SymbolDisplayFormat` that the helper does not offer keeps its call sites under the pragma with a
  reason naming the option that differs.
- **The naming analyzer.** `TypeNameHandlingAnalyzer` (in `Whizbang.Generators`, so it runs on
  the framework and on every consumer) reports the residue the bans cannot see, as warnings:
  WHIZ160 a name composed with `+` or `, ` by hand, WHIZ161 a name dissected by `Split`,
  `Substring`, `IndexOf('+')` or `Replace("global::", ...)`, WHIZ162 two type-name strings compared
  with `==` or `string.Equals`, WHIZ163 a member or parameter named `*ClrTypeName*`, `*TypeName*`
  or `*EventType*` assigned from an interpolated or concatenated string. The helper classes are
  exempt. The framework's own test projects turn the four rules off in `tests/.editorconfig`:
  fixtures compose names on purpose, including malformed ones that prove the parsers' tolerance.
- **The mirror contract.** `tests/Whizbang.Generators.Tests/TypeNameMirrorContractTests.cs`
  fails the generator tests when the two sides drift on any shape in its corpus.
- Deferred: typed keys (`ClrTypeName` and `WireTypeName` value objects constructible only through
  `TypeNameFormatter`). With the bans and the analyzer in place a bypass is already a build
  error; the typed keys would add compile-time shape to the sinks
  (`PerspectiveRetentionDeclaration`, `PerspectiveRegistrationInfo`, the coordinator's
  `clr_type_name` parameters) at the cost of a public API change that reaches generated code.

## Checklist when you touch a naming site

1. Is the string a key (persisted, compared, or routed on)? Then it comes from the helper for that
   form, on that side.
2. Is it compared against a persisted value? Then it goes through `EventTypeMatchingHelper`.
3. Did you add a shape (a new form, a new column)? Add it to the table above, to both helpers,
   and to the mirror-contract corpus.
4. Did you write a test? Use a nested model or event in it; a top-level type hides every one of
   these bugs.
