// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

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
  private static readonly ConcurrentDictionary<Type, StreamIdTypeEntry> _entries = new();
  private static readonly ConcurrentDictionary<Type, Resolved> _resolved = new();

  /// <summary>Registrations made visible so far: what <see cref="Generation"/> reports.</summary>
  private static long _published;

  /// <summary>Runs on the registering thread after its entries are visible and before they are counted; tests only.</summary>
  [ThreadStatic]
  private static Action? _beforePublishForTests;

  /// <summary>An answer and the generation it was computed at; null <see cref="Entry"/> when no entry answers.</summary>
  private sealed record Resolved(StreamIdTypeEntry? Entry, long AtGeneration);

  /// <summary>Registers the entries of one assembly. A type that already has an entry keeps it.</summary>
  /// <param name="entries">One entry per message type the assembly compiles.</param>
  /// <remarks>
  /// A type is compiled in one assembly and so has one entry; a second registration of it (a test, a reloaded module)
  /// is ignored rather than allowed to change an answer already given. The generation advances after the entries are
  /// in place, never before, so a lookup that reads the new generation always sees them.
  /// </remarks>
  public static void Register(params StreamIdTypeEntry[] entries) {
    ArgumentNullException.ThrowIfNull(entries);
    foreach (var entry in entries) {
      _entries.TryAdd(entry.MessageType, entry);
    }
    _beforePublishForTests?.Invoke();
    Interlocked.Increment(ref _published);
  }

  /// <summary>The entry that answers for <paramref name="messageType"/>: its own, else its nearest registered base type's.</summary>
  /// <param name="messageType">The runtime type of a message.</param>
  /// <returns>The entry, or null when neither the type nor any base type has one.</returns>
  public static StreamIdTypeEntry? Resolve(Type messageType) {
    ArgumentNullException.ThrowIfNull(messageType);

    // Read before the entries, so an answer computed while a registration lands is recorded against the earlier
    // generation and recomputed on the next lookup.
    var generation = Generation;
    if (_resolved.TryGetValue(messageType, out var cached) && cached.AtGeneration == generation) {
      return cached.Entry;
    }

    StreamIdTypeEntry? found = null;
    for (var type = messageType; type is not null && found is null; type = type.BaseType) {
      _entries.TryGetValue(type, out found);
    }
    _resolved[messageType] = new Resolved(found, generation);
    return found;
  }

  /// <summary>
  /// Runs <paramref name="callback"/> inside every registration this thread makes until the returned scope is disposed,
  /// after the registration's entries are visible and before <see cref="Generation"/> counts it, so a test can resolve
  /// inside that window without depending on how two threads interleave (#1900).
  /// </summary>
  internal static IDisposable BeforePublishForTests(Action callback) {
    var previous = _beforePublishForTests;
    _setBeforePublish(callback);
    return new Scope(previous);
  }

  private static void _setBeforePublish(Action? callback) => _beforePublishForTests = callback;

  private sealed class Scope(Action? previous) : IDisposable {
    public void Dispose() => _setBeforePublish(previous);
  }

  /// <summary>How many registrations are visible; a cached answer computed at an earlier value is recomputed.</summary>
  public static long Generation => Interlocked.Read(ref _published);
}
