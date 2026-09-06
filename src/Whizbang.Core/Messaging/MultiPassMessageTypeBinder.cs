using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
    var p1 = _tryGetType(assemblyQualifiedName);
    if (p1 != null) {
      return (p1, MessageTypeBinderPass.ExactStrongName);
    }

    // Pass 2 — strip Version/Culture/PublicKeyToken via existing helper, retry Type.GetType.
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(assemblyQualifiedName);
    if (!string.Equals(normalized, assemblyQualifiedName, StringComparison.Ordinal)) {
      var p2 = _tryGetType(normalized);
      if (p2 != null) {
        return (p2, MessageTypeBinderPass.AssemblySimpleName);
      }
    }

    // Pass 3 — search loaded assemblies for a type whose FullName matches, ignoring the
    // declared assembly name entirely. Handles the assembly-rename case.
    var fullName = _extractTypeFullName(assemblyQualifiedName);
    if (!string.IsNullOrEmpty(fullName)) {
      foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
        var found = _tryGetTypeFrom(asm, fullName);
        if (found != null) {
          return (found, MessageTypeBinderPass.TypeFullNameAcrossAssemblies);
        }
      }
    }

    return (null, MessageTypeBinderPass.Miss);
  }

  /// <summary>
  /// <see cref="Type.GetType(string, bool)"/> with <c>throwOnError: false</c> only suppresses
  /// <see cref="TypeLoadException"/> — the type not being found. It does NOT suppress the
  /// exceptions raised while the NAME itself is being parsed, before any lookup happens: a
  /// malformed assembly segment (an unparseable <c>Version=</c>, say) surfaces as
  /// <see cref="FileLoadException"/>, and a name that parses but names something unloadable
  /// surfaces as <see cref="BadImageFormatException"/> or <see cref="ArgumentException"/>.
  ///
  /// Those escaping is the opposite of what this binder is for. The whole point of the three-pass
  /// design is that a type header which does not resolve comes back as a <c>Miss</c> the caller
  /// can report and route to the dead letter queue. Letting a parse failure throw instead skips
  /// passes 2 and 3 — which would very likely have resolved the type, since stripping the
  /// malformed metadata is exactly what pass 2 does — and skips the cache write, so every
  /// redelivery of that message pays the same throw again.
  /// </summary>
  private static Type? _tryGetType(string assemblyQualifiedName) {
    try {
      return Type.GetType(assemblyQualifiedName, throwOnError: false);
    } catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or ArgumentException) {
      return null;
    }
  }

  /// <summary>
  /// The pass-3 counterpart of <see cref="_tryGetType"/>. One assembly rejecting the name must not
  /// abandon the search across the others.
  ///
  /// Measured difference from pass 1: <see cref="Assembly.GetType(string, bool, bool)"/> does NOT
  /// throw on a name that fails to parse -- a malformed nested assembly segment that makes
  /// <see cref="Type.GetType(string, bool)"/> raise <see cref="FileLoadException"/> comes back as
  /// null here, because the assembly is already in hand and only the nested argument's assembly
  /// name is left to resolve. So this guard does not fire on malformed input, and no unit test
  /// reaches it (see residue BT).
  ///
  /// It stays because the documented triggers are real and are not about the name at all: a
  /// nested argument naming an assembly that EXISTS but fails to load raises
  /// <see cref="FileLoadException"/>, and one built for another architecture raises
  /// <see cref="BadImageFormatException"/>. Both are properties of the deployment, not of the
  /// message -- and the binder's contract is that no header can make it throw.
  /// </summary>
  private static Type? _tryGetTypeFrom(Assembly assembly, string fullName) {
    try {
      return assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
    } catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or ArgumentException) {
      return null;
    }
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
