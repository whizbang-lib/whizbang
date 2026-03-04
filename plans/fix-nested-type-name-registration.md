# Fix: Nested Type Name Registration in MessageJsonContextGenerator

## Problem

The `MessageJsonContextGenerator` registers nested type names using C# syntax (dots `.`) instead of CLR syntax (plus signs `+`), causing type resolution failures at runtime.

### Example

For a nested type like `JDX.Contracts.Auth.AuthContracts.LoginAttemptCommand`:

**Generated registration (WRONG):**
```csharp
global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterTypeName(
  "JDX.Contracts.Auth.AuthContracts.LoginAttemptCommand, JDX.Contracts",  // Uses dots
  typeof(global::JDX.Contracts.Auth.AuthContracts.LoginAttemptCommand),
  MessageJsonContext.Default);
```

**Runtime lookup uses CLR format:**
```
JDX.Contracts.Auth.AuthContracts+LoginAttemptCommand, JDX.Contracts
```

After `NormalizeTypeName` strips version info:
- **Registered key**: `JDX.Contracts.Auth.AuthContracts.LoginAttemptCommand, JDX.Contracts` (dots)
- **Lookup key**: `JDX.Contracts.Auth.AuthContracts+LoginAttemptCommand, JDX.Contracts` (plus)

These don't match, so `GetTypeInfoByName` returns null and we get:
```
Failed to resolve message type 'JDX.Contracts.Auth.AuthContracts+LoginAttemptCommand, JDX.Contracts'.
Ensure the assembly containing this type is loaded and registered via [ModuleInitializer].
```

## Root Cause

In `MessageJsonContextGenerator.cs` lines 1346-1354:

```csharp
var typeRegistrations = messageTypes.Select(message => {
  var typeNameWithoutGlobal = message.FullyQualifiedName.Replace(PLACEHOLDER_GLOBAL, "");
  var assemblyQualifiedName = $"{typeNameWithoutGlobal}, {actualAssemblyName}";

  return $"  global::Whizbang.Core.Serialization.JsonContextRegistry.RegisterTypeName(\n" +
         $"    \"{assemblyQualifiedName}\",\n" +   // <-- This uses dots for nested types
         $"    typeof({message.FullyQualifiedName}),\n" +
         $"    MessageJsonContext.Default);";
});
```

`message.FullyQualifiedName` comes from Roslyn and uses C# syntax (dots), but the registered string key should match .NET's `Type.FullName` format which uses `+` for nested types.

## Solution Options

### Option 1: Fix in Generator (Recommended)

Convert the registered type name string to use CLR format (plus for nested types).

In Roslyn, we can detect nested types via `INamedTypeSymbol.ContainingType != null`.

**Pseudocode:**
```csharp
// When building the type name string for registration:
string GetClrTypeName(INamedTypeSymbol symbol) {
  if (symbol.ContainingType != null) {
    // Nested type - use + separator
    return GetClrTypeName(symbol.ContainingType) + "+" + symbol.Name;
  } else if (symbol.ContainingNamespace != null) {
    return symbol.ContainingNamespace.ToDisplayString() + "." + symbol.Name;
  }
  return symbol.Name;
}
```

**Or use ToDisplayString with appropriate format:**
```csharp
var clrFormat = new SymbolDisplayFormat(
  typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
  // Plus sign for nested types
  memberOptions: SymbolDisplayMemberOptions.None
);
```

Note: Check if Roslyn has a display format that outputs CLR-style names with `+`.

### Option 2: Fix in NormalizeTypeName

Normalize both formats to a consistent format in `EventTypeMatchingHelper.NormalizeTypeName`.

This is more fragile because:
- Need to distinguish namespace dots from nested type dots
- Would need to know type structure from string alone (not possible without metadata)

**NOT recommended** - better to generate correct format from the start.

## Files to Modify

1. **`src/Whizbang.Generators/MessageJsonContextGenerator.cs`**
   - Lines 1346-1354: Type registration loop
   - Lines 1372-1382: MessageEnvelope registration loop
   - Lines 592-596: GetTypeInfoByName switch generation

2. **Tests to add:**
   - `tests/Whizbang.Generators.Tests/MessageJsonContextGeneratorTests.cs`
   - Test case: `Generator_WithNestedTypes_UsesClrFormatWithPlusSign`

## Test Case

```csharp
// Arrange - nested message type
public static class AuthContracts {
  public class LoginAttemptCommand : IMessage { }
}

// Assert - generated registration should use +
// Expected: "MyApp.AuthContracts+LoginAttemptCommand, MyApp"
// NOT: "MyApp.AuthContracts.LoginAttemptCommand, MyApp"
```

## Related Code

- `JsonContextRegistry.RegisterTypeName()` - stores with normalized key
- `JsonContextRegistry.GetTypeInfoByName()` - looks up with normalized key
- `EventTypeMatchingHelper.NormalizeTypeName()` - normalizes (strips version, but doesn't convert +/.)
- `EnvelopeSerializer.DeserializeMessage()` - calls GetTypeInfoByName with runtime type name

## Impact

This bug affects any application using nested message types (classes defined inside other classes). The `IDispatcher.SendAsync()` call fails when trying to serialize the message because the type can't be resolved from the stored type name.

## Discovered In

JDNext migration - `ExchangeCodeEndpoint` sending `LoginAttemptCommand` which is nested in `AuthContracts` class.

## Temporary Workaround Applied

A workaround has been applied in `EventTypeMatchingHelper.NormalizeTypeName()` to convert `+` to `.` during normalization. This makes both CLR format (`+`) and C# format (`.`) normalize to the same string, allowing type lookup to succeed.

**File**: `src/Whizbang.Core/Messaging/EventTypeMatchingHelper.cs`
**Change**: Added `result = result.Replace("+", ".");` after stripping version info.

This workaround should be kept even after the generator is fixed, as it provides backwards compatibility for any stored type names that use the `+` format.

## TODO

The proper fix in the generator is still needed for consistency. The workaround works but means:
1. Stored type names may vary (`+` vs `.`) depending on source
2. The generator should still be updated to use the canonical CLR format (`+`)
3. Add a test case for nested types in `MessageJsonContextGeneratorTests.cs`
