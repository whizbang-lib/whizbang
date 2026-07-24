using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Three-pass cascade implementation of <see cref="IMessageTypeBinder"/>. Caches every
/// successful resolution per process so subsequent lookups are O(1) regardless of which
/// pass produced the original hit.
/// </summary>
/// <remarks>
/// Reflection over loaded assemblies is contained to the binder. Whizbang.Core is otherwise
/// reflection-free; this is an intentional, isolated, AOT-warning-suppressed island for
/// type-by-name resolution that the source generators can't pre-compute (the input is a
/// runtime-supplied string from a publisher's envelope).
/// </remarks>
[SuppressMessage("Trimming", "IL2057:Unrecognized value passed to the parameter 'typeName' of method 'Type.GetType'",
  Justification = "Type names come from publisher envelopes at runtime; deliberate boundary case.")]
[SuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode for Assembly.GetType",
  Justification = "Type lookup over loaded assemblies is the binder's whole job; trimmer cannot statically prove which types are needed for runtime envelope type names.")]
[SuppressMessage("AOT", "IL3050:Calling members annotated with 'RequiresDynamicCode'",
  Justification = "Type lookup over loaded assemblies is the binder's whole job; assemblies are AOT-included.")]
public sealed class MultiPassMessageTypeBinder : IMessageTypeBinder {
  private readonly ConcurrentDictionary<string, (Type? Type, MessageTypeBinderPass Pass)> _cache = new(StringComparer.Ordinal);

  /// <inheritdoc />
  public Type? Bind(string assemblyQualifiedName) => BindWithDiagnostics(assemblyQualifiedName).Type;

  /// <inheritdoc />
  public (Type? Type, MessageTypeBinderPass Pass) BindWithDiagnostics(string assemblyQualifiedName) {
    if (string.IsNullOrEmpty(assemblyQualifiedName)) {
      return (null, MessageTypeBinderPass.Miss);
    }

    if (_cache.TryGetValue(assemblyQualifiedName, out var cached)) {
      return cached;
    }

    var result = _resolve(assemblyQualifiedName);
    _cache[assemblyQualifiedName] = result;
    return result;
  }

  private static (Type? Type, MessageTypeBinderPass Pass) _resolve(string assemblyQualifiedName) {
    // Pass 1 — full strong name (Type.GetType honours Version/Culture/PublicKeyToken).
    var p1 = Type.GetType(assemblyQualifiedName, throwOnError: false);
    if (p1 != null) {
      return (p1, MessageTypeBinderPass.ExactStrongName);
    }

    // Pass 2 — strip Version/Culture/PublicKeyToken via existing helper, retry Type.GetType.
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(assemblyQualifiedName);
    if (!string.Equals(normalized, assemblyQualifiedName, StringComparison.Ordinal)) {
      var p2 = Type.GetType(normalized, throwOnError: false);
      if (p2 != null) {
        return (p2, MessageTypeBinderPass.AssemblySimpleName);
      }
    }

    // Pass 3 — search loaded assemblies for a type whose FullName matches, ignoring the
    // declared assembly name entirely. Handles the assembly-rename case.
    var fullName = _extractTypeFullName(assemblyQualifiedName);
    if (!string.IsNullOrEmpty(fullName)) {
      foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
        var found = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
        if (found != null) {
          return (found, MessageTypeBinderPass.TypeFullNameAcrossAssemblies);
        }
      }
    }

    return (null, MessageTypeBinderPass.Miss);
  }

  private static string? _extractTypeFullName(string assemblyQualifiedName) {
    // For "Namespace.Type, Assembly, Version=..." → "Namespace.Type"
    // For generic "Outer`1[[Inner.Type, Inner.Asm]], Outer.Asm" → "Outer`1[[Inner.Type, Inner.Asm]]"
    //   (we leave the inner generic args intact; Assembly.GetType handles those when matched).
    // Strategy: find the first comma OUTSIDE of any [[...]] brackets and cut there.
    var depth = 0;
    for (var i = 0; i < assemblyQualifiedName.Length; i++) {
      var c = assemblyQualifiedName[i];
      if (c == '[') {
        depth++;
      } else if (c == ']') {
        depth--;
      } else if (c == ',' && depth == 0) {
        return assemblyQualifiedName[..i].Trim();
      }
    }
    return assemblyQualifiedName.Trim();
  }
}
