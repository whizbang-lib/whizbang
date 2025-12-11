# Diagnostic System

**Reporting diagnostics from source generators**

This document explains how to define and report diagnostics (info, warnings, errors) from source generators using DiagnosticDescriptors.

---

## Table of Contents

1. [Overview](#overview)
2. [Diagnostic Descriptor Pattern](#diagnostic-descriptor-pattern)
3. [Diagnostic ID Allocation](#diagnostic-id-allocation)
4. [Reporting Diagnostics](#reporting-diagnostics)
5. [Severity Levels](#severity-levels)
6. [Best Practices](#best-practices)

---

## Overview

### What Are Diagnostics?

**Diagnostics are messages from generators to developers**:
- **Info**: Informational messages (discoveries, etc.)
- **Warning**: Potential issues (no receptors found, etc.)
- **Error**: Fatal problems (break compilation)

**Where they appear**:
- IDE (Visual Studio, VS Code, Rider)
- Build output (console)
- Error list window
- Squiggles in code

---

### Why Use DiagnosticDescriptors?

**Benefits**:
- Centralized definition (one place for all diagnostics)
- Consistent format and messaging
- Unique IDs for tracking
- Localization support (future)
- Documentation integration

**Alternative (bad)**:
```csharp
// ❌ WRONG: Hard-coded diagnostic
context.ReportDiagnostic(Diagnostic.Create(
    new DiagnosticDescriptor(
        "WHIZ001",  // Hard-coded ID
        "Receptor Discovered",  // Hard-coded title
        "Found receptor...",  // Hard-coded message
        "Whizbang",  // Hard-coded category
        DiagnosticSeverity.Info,
        true
    ),
    Location.None
));
```

**Why this is bad**:
- Duplicated code
- Hard to maintain
- Easy to create ID conflicts
- No central documentation

---

## Diagnostic Descriptor Pattern

### DiagnosticDescriptors.cs

**All diagnostics defined in one file**:

```csharp
// DiagnosticDescriptors.cs
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

internal static class DiagnosticDescriptors {
  private const string CATEGORY = "Whizbang.SourceGeneration";

  /// <summary>
  /// WHIZ001: Info - Receptor discovered during source generation.
  /// </summary>
  public static readonly DiagnosticDescriptor ReceptorDiscovered = new(
      id: "WHIZ001",
      title: "Receptor Discovered",
      messageFormat: "Found receptor '{0}' handling {1} → {2}",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Info,
      isEnabledByDefault: true,
      description: "A receptor implementation was discovered and will be registered."
  );

  /// <summary>
  /// WHIZ002: Warning - No receptors found in the compilation.
  /// </summary>
  public static readonly DiagnosticDescriptor NoReceptorsFound = new(
      id: "WHIZ002",
      title: "No Receptors Found",
      messageFormat: "No IReceptor implementations were found in the compilation",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Warning,
      isEnabledByDefault: true,
      description: "The source generator did not find any classes implementing IReceptor<TMessage, TResponse>."
  );

  /// <summary>
  /// WHIZ003: Error - Receptor must have exactly two type parameters.
  /// </summary>
  public static readonly DiagnosticDescriptor InvalidReceptorTypeParameters = new(
      id: "WHIZ003",
      title: "Invalid Receptor Type Parameters",
      messageFormat: "Receptor '{0}' must implement IReceptor<TMessage, TResponse> with exactly two type parameters",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "IReceptor interface requires exactly two type parameters: TMessage and TResponse."
  );
}
```

---

### Key Parts

**ID**:
```csharp
id: "WHIZ001"
```
- Unique identifier for diagnostic
- Format: PREFIX + NUMBER
- Whizbang uses "WHIZ" prefix
- See [Diagnostic ID Allocation](#diagnostic-id-allocation)

**Title**:
```csharp
title: "Receptor Discovered"
```
- Short description (shown in error list)
- 2-5 words
- Title case

**Message Format**:
```csharp
messageFormat: "Found receptor '{0}' handling {1} → {2}"
```
- Message shown to developer
- Use placeholders `{0}`, `{1}`, etc. for arguments
- Clear, concise, actionable

**Category**:
```csharp
category: "Whizbang.SourceGeneration"
```
- Groups related diagnostics
- Format: `Library.Feature`
- All Whizbang generators use same category

**Severity**:
```csharp
defaultSeverity: DiagnosticSeverity.Info
```
- `Info`, `Warning`, or `Error`
- See [Severity Levels](#severity-levels)

**Description**:
```csharp
description: "A receptor implementation was discovered and will be registered."
```
- Longer explanation
- Shown in diagnostic details
- 1-2 sentences

---

## Diagnostic ID Allocation

### Whizbang ID Ranges

**Current allocation**:

| Range | Feature | Status |
|-------|---------|--------|
| WHIZ001-003 | Receptor Discovery | Allocated |
| WHIZ004-006 | Aggregate ID System | Reserved |
| WHIZ007-009 | Message Registry | Reserved |
| WHIZ010+ | Future Features | Available |

---

### Allocation Guidelines

**When adding new diagnostic**:
1. Check `DiagnosticDescriptors.cs` for next available ID
2. Reserve 2-3 IDs per feature (room for future additions)
3. Document in this file (update table above)
4. Use sequential numbering

**Example**:
```csharp
// Adding new feature: Perspective Discovery
// Next available: WHIZ010

/// <summary>
/// WHIZ010: Info - Perspective discovered.
/// </summary>
public static readonly DiagnosticDescriptor PerspectiveDiscovered = new(...);

/// <summary>
/// WHIZ011: Warning - Perspective missing checkpoint property.
/// </summary>
public static readonly DiagnosticDescriptor PerspectiveMissingCheckpoint = new(...);

// Reserve WHIZ012 for future perspective diagnostics
```

---

## Reporting Diagnostics

### Basic Reporting

```csharp
context.ReportDiagnostic(Diagnostic.Create(
    DiagnosticDescriptors.ReceptorDiscovered,
    Location.None,
    "OrderReceptor",  // Arg 0
    "CreateOrder",    // Arg 1
    "OrderCreated"    // Arg 2
));
```

**Result**:
```
Info WHIZ001: Found receptor 'OrderReceptor' handling CreateOrder → OrderCreated
```

---

### Reporting at Specific Location

```csharp
var classDeclaration = (ClassDeclarationSyntax)context.Node;
var location = classDeclaration.Identifier.GetLocation();

context.ReportDiagnostic(Diagnostic.Create(
    DiagnosticDescriptors.InvalidReceptorTypeParameters,
    location,  // Shows squiggle at class name
    classDeclaration.Identifier.Text
));
```

**Result**:
- Error squiggle under class name in IDE
- Click error → jumps to exact location
- Includes file path and line number

---

### Reporting Multiple Diagnostics

```csharp
private static void GenerateDispatcherCode(
    SourceProductionContext context,
    ImmutableArray<ReceptorInfo> receptors) {

  if (receptors.IsEmpty) {
    // Warning: No receptors found
    context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.NoReceptorsFound,
        Location.None
    ));
    return;
  }

  // Info: Report each discovered receptor
  foreach (var receptor in receptors) {
    context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.ReceptorDiscovered,
        Location.None,
        receptor.ClassName,
        receptor.MessageType,
        receptor.ResponseType
    ));
  }

  // Generate code...
}
```

---

### Location from Syntax

**Get location from syntax node**:
```csharp
// Entire class declaration
var location = classDeclaration.GetLocation();

// Just the class name (more precise)
var location = classDeclaration.Identifier.GetLocation();

// Specific property
var propertyDeclaration = ...;
var location = propertyDeclaration.Identifier.GetLocation();

// Specific attribute
var attributeSyntax = ...;
var location = attributeSyntax.GetLocation();
```

---

## Severity Levels

### Info

**When to use**:
- Discovery notifications
- Informational messages
- Non-critical status updates

**Behavior**:
- Shows in build output
- Shows in error list (if enabled)
- Does NOT break build
- Typically hidden in IDE by default

**Example**:
```csharp
DiagnosticSeverity.Info
// "Found receptor 'OrderReceptor'"
```

---

### Warning

**When to use**:
- Potential issues
- Missing recommended elements
- Deprecated usage
- Configuration suggestions

**Behavior**:
- Shows in build output
- Shows in error list (yellow icon)
- Does NOT break build
- Visible in IDE

**Example**:
```csharp
DiagnosticSeverity.Warning
// "No receptors found in compilation"
```

---

### Error

**When to use**:
- Invalid code
- Missing required elements
- Compilation-breaking issues

**Behavior**:
- Shows in build output (red)
- Shows in error list (red icon)
- BREAKS build
- Squiggle in IDE

**Example**:
```csharp
DiagnosticSeverity.Error
// "Receptor must have exactly two type parameters"
```

---

## Best Practices

### Message Format Guidelines

**Do**:
- ✅ Use clear, actionable language
- ✅ Include specific details (type names, file paths)
- ✅ Suggest fixes when possible
- ✅ Keep messages concise (1-2 sentences)

**Don't**:
- ❌ Use technical jargon unnecessarily
- ❌ Write vague messages ("Something went wrong")
- ❌ Include stack traces in user-facing messages
- ❌ Use ALL CAPS (except acronyms)

---

### Examples

**Good messages**:
```csharp
// ✅ Clear and specific
messageFormat: "Found receptor '{0}' handling {1} → {2}"

// ✅ Actionable suggestion
messageFormat: "Receptor '{0}' must implement IReceptor<TMessage, TResponse>"

// ✅ Explains why
messageFormat: "No receptors found - dispatcher will have no routing logic"
```

**Bad messages**:
```csharp
// ❌ Vague
messageFormat: "Something is wrong with {0}"

// ❌ Too technical
messageFormat: "Failed to resolve symbol for INamedTypeSymbol at semantic model index {0}"

// ❌ Not actionable
messageFormat: "Error occurred"
```

---

### XML Documentation

**Always document diagnostic descriptors**:

```csharp
/// <summary>
/// WHIZ001: Info - Receptor discovered during source generation.
/// </summary>
/// <remarks>
/// This diagnostic is reported for each receptor implementation found
/// during source generation. It confirms that the receptor will be
/// registered in the generated dispatcher.
/// </remarks>
public static readonly DiagnosticDescriptor ReceptorDiscovered = new(...);
```

**Include**:
- `<summary>` with ID, severity, and one-line description
- `<remarks>` with detailed explanation (when needed)
- Usage examples (when helpful)

---

### Diagnostic Categories

**Use consistent categories**:
```csharp
// ✅ CORRECT: Consistent category
private const string CATEGORY = "Whizbang.SourceGeneration";

// All diagnostics use same category
public static readonly DiagnosticDescriptor Diagnostic1 = new(..., CATEGORY, ...);
public static readonly DiagnosticDescriptor Diagnostic2 = new(..., CATEGORY, ...);
```

**Don't**:
```csharp
// ❌ WRONG: Inconsistent categories
public static readonly DiagnosticDescriptor Diagnostic1 = new(..., "Whizbang", ...);
public static readonly DiagnosticDescriptor Diagnostic2 = new(..., "Generator", ...);
public static readonly DiagnosticDescriptor Diagnostic3 = new(..., "SourceGen", ...);
```

---

### isEnabledByDefault

**Default to `true`**:
```csharp
isEnabledByDefault: true
```

**Only use `false` for**:
- Experimental diagnostics
- Very verbose diagnostics
- Debug-only diagnostics

**Most diagnostics should be enabled by default.**

---

## Checklist

Before adding new diagnostic:

- [ ] Added to `DiagnosticDescriptors.cs`
- [ ] Unique ID allocated (checked against existing IDs)
- [ ] ID documented in allocation table
- [ ] Clear, actionable message format
- [ ] Appropriate severity level
- [ ] XML documentation included
- [ ] Uses consistent category (`Whizbang.SourceGeneration`)
- [ ] `isEnabledByDefault: true` (unless good reason)
- [ ] Tested in generator

---

## See Also

- [generator-patterns.md](generator-patterns.md) - Where to report diagnostics in generators
- [quick-reference.md](quick-reference.md) - Diagnostic checklist
