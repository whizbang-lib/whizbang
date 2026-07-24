# Refactor MessageRegistryGenerator to Typed C# Model

## Context

The `MessageRegistryGenerator` currently builds JSON using `StringBuilder` string templates with placeholder replacements (`__MESSAGE_TYPE__`, `__FILE_PATH__`, etc.). This works but is fragile — adding a new field requires editing snippet templates, placeholder constants, and string building logic in multiple places.

## Proposal

Replace string template JSON building with a proper typed C# object model:

1. Define C# records matching the JSON schema
2. Populate them from the Roslyn analysis (same discovery logic)
3. Serialize to JSON with `System.Text.Json.JsonSerializer`
4. Emit the serialized JSON into the generated C# constant

## Benefits

- **Maintainability**: Adding a field = adding a property to a record (one place)
- **Correctness**: No malformed JSON from string concatenation
- **Testability**: Assert on C# objects, not parse JSON in tests
- **Reuse**: The model records could be shared with the C# LSP server
- **No performance impact**: Serialization is microseconds, bottleneck is Roslyn analysis

## Typed Model

```csharp
// Could live in Whizbang.Generators.Shared or a new shared project
internal sealed record MessageRegistryModel {
  public required IReadOnlyList<MessageEntry> Messages { get; init; }
  public required IReadOnlyList<PackageEntry> WhizbangPackages { get; init; }
}

internal sealed record MessageEntry {
  public required string Type { get; init; }
  public bool IsCommand { get; init; }
  public bool IsEvent { get; init; }
  public string? FilePath { get; init; }
  public int LineNumber { get; init; }
  public string? DocsUrl { get; init; }
  public IReadOnlyList<TestEntry>? Tests { get; init; }
  public IReadOnlyList<LocationEntry>? Dispatchers { get; init; }
  public IReadOnlyList<LocationEntry>? Receptors { get; init; }
  public IReadOnlyList<LocationEntry>? Perspectives { get; init; }
}

internal sealed record LocationEntry {
  public required string Class { get; init; }
  public required string Method { get; init; }
  public required string FilePath { get; init; }
  public int LineNumber { get; init; }
  public string? DocsUrl { get; init; }
  public IReadOnlyList<TestEntry>? Tests { get; init; }
}

internal sealed record TestEntry {
  public required string TestFile { get; init; }
  public required string TestMethod { get; init; }
  public int TestLine { get; init; }
  public required string TestClass { get; init; }
}

internal sealed record PackageEntry {
  public required string Id { get; init; }
  public required string VersionPrefix { get; init; }
}
```

## Implementation

1. Define the model records in `MessageRegistryGenerator.cs` (bottom, with existing records)
2. Replace the `_generateMessageRegistry` method body:
   - Keep all discovery/enrichment logic (docsMap, testsMap lookups)
   - Build `MessageRegistryModel` instead of `StringBuilder`
   - Serialize with `JsonSerializer.Serialize(model, jsonOptions)`
   - Use `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }`
3. Remove snippet templates: `MESSAGE_ENTRY_HEADER`, `DISPATCHER_ENTRY`, `RECEPTOR_ENTRY`, `PERSPECTIVE_ENTRY`, `MESSAGE_ENTRY_FOOTER`, `TEST_ENTRY`, `JSON_ARRAY_WRAPPER` (keep `CSHARP_WRAPPER`)
4. Remove placeholder constants (`PLACEHOLDER_MESSAGE_TYPE`, etc.)
5. Update tests

## Files to Change

- `src/Whizbang.Generators/MessageRegistryGenerator.cs` — main refactor
- `src/Whizbang.Generators/Templates/Snippets/MessageRegistrySnippets.cs` — remove most snippets, keep CSHARP_WRAPPER
- `tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs` — update assertions

## Effort

Medium — ~2 hours. The discovery logic stays the same, only the output format changes.
