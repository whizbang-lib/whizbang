using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Resolves an assembly-qualified CLR type name string to a runtime <see cref="Type"/>,
/// walking a fallback cascade so that contracts assembly drift doesn't break receivers.
/// Slice 4 of the resilient-transport plan.
/// </summary>
/// <remarks>
/// <para>
/// Implementations should walk these passes in order, returning the first hit:
/// </para>
/// <list type="number">
///   <item><description>Pass 1 — Full strong name (Name + Version + Culture + PublicKeyToken).</description></item>
///   <item><description>Pass 2 — Assembly simple name only; ignore version/culture/public-key-token.</description></item>
///   <item><description>Pass 3 — Type <c>FullName</c> across all loaded assemblies (handles assembly-rename case).</description></item>
/// </list>
/// <para>
/// A miss across all three returns null. The caller decides what to do — typically fall
/// back to the slice 5 raw-receptor path or ack + drop with a structured log.
/// </para>
/// </remarks>
/// <docs>fundamentals/serialization/type-binding</docs>
public interface IMessageTypeBinder {
  /// <summary>
  /// Resolves <paramref name="assemblyQualifiedName"/> to a <see cref="Type"/> via the
  /// pass cascade. Returns null when nothing matches at any pass.
  /// </summary>
  Type? Bind(string assemblyQualifiedName);

  /// <summary>
  /// Resolves <paramref name="assemblyQualifiedName"/> and reports which pass produced
  /// the hit (or that all passes missed). Useful for telemetry — sustained pass-3 hit
  /// rate signals a coordinated assembly-rename is overdue.
  /// </summary>
  (Type? Type, MessageTypeBinderPass Pass) BindWithDiagnostics(string assemblyQualifiedName);
}

/// <summary>
/// Indicates which pass of <see cref="IMessageTypeBinder.BindWithDiagnostics"/> resolved a type.
/// </summary>
public enum MessageTypeBinderPass {
  /// <summary>No pass matched — caller should treat as unresolvable.</summary>
  Miss = 0,

  /// <summary>Pass 1 — full strong name match (Name + Version + Culture + PublicKeyToken).</summary>
  ExactStrongName = 1,

  /// <summary>Pass 2 — assembly simple-name match (Version/Culture/PublicKeyToken stripped).</summary>
  AssemblySimpleName = 2,

  /// <summary>Pass 3 — type FullName matched in some loaded assembly regardless of declared assembly name.</summary>
  TypeFullNameAcrossAssemblies = 3,
}
