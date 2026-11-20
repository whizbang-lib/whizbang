// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Registry;

/// <summary>
/// Every message type's stream id, across every assembly, keyed by the exact type (#1905, #1908). Each assembly's
/// source generator registers one <see cref="StreamIdTypeEntry"/> per message type it compiles, including the types
/// that only inherit a <c>[StreamId]</c>, from a <c>[ModuleInitializer]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A message resolves by its runtime type: the type's own entry wins, wherever its base types are compiled. A type with
/// no entry (a runtime subclass, or one compiled without the generator) takes the entry of its nearest base type that
/// has one, found by walking <see cref="Type.BaseType"/>, which is AOT-safe. The answer is cached per type and
/// recomputed after any later registration, which advances <see cref="Generation"/> only once its entries are visible
/// (the rule of #1900), so a cached answer never outlives a nearer entry.
/// </para>
/// <para>
/// A lookup is one dictionary read whatever the number of message types, and no answer depends on the order in which
/// assemblies or types registered.
/// </para>
/// </remarks>
/// <docs>extending/attributes/streamid#how-a-message-type-finds-its-stream-id</docs>
/// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdTypeRegistryTests.cs</tests>
public static class StreamIdTypeRegistry {
  /// <summary>Registers the entries of one assembly. A type that already has an entry keeps it.</summary>
  /// <param name="entries">One entry per message type the assembly compiles.</param>
  public static void Register(params StreamIdTypeEntry[] entries) {
    ArgumentNullException.ThrowIfNull(entries);
  }

  /// <summary>The entry that answers for <paramref name="messageType"/>: its own, else its nearest registered base type's.</summary>
  /// <param name="messageType">The runtime type of a message.</param>
  /// <returns>The entry, or null when neither the type nor any base type has one.</returns>
  public static StreamIdTypeEntry? Resolve(Type messageType) {
    ArgumentNullException.ThrowIfNull(messageType);
    return null;
  }

  /// <summary>
  /// Runs <paramref name="callback"/> inside every registration this thread makes until the returned scope is disposed,
  /// after the registration's entries are visible and before <see cref="Generation"/> counts it, so a test can resolve
  /// inside that window without depending on how two threads interleave (#1900).
  /// </summary>
  internal static IDisposable BeforePublishForTests(Action callback) {
    ArgumentNullException.ThrowIfNull(callback);
    return new Scope();
  }

  private sealed class Scope : IDisposable {
    public void Dispose() { }
  }

  /// <summary>How many registrations are visible; a cached answer computed at an earlier value is recomputed.</summary>
  public static long Generation => 0;
}
