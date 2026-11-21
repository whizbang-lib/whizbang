// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using BenchmarkDotNet.Attributes;
using Whizbang.Core.Registry;

namespace Whizbang.Benchmarks;

/// <summary>
/// Whether resolving a message's stream id costs the same however many message types are registered (#1908). Not a gate.
/// </summary>
/// <remarks>
/// <para>
/// The per-type table (<see cref="StreamIdTypeRegistry"/>) answers a message by one lookup on its exact runtime type,
/// and a type with no entry of its own by a cached walk of its base types. The generated extractor it replaces answered
/// by a chain of <c>is</c> checks, one per message type, so its cost grew with the number of types; the chain is
/// modeled here by <see cref="Type.IsInstanceOfType"/> over the same types, with the message's type last, as the
/// most-derived-first order put a base type's check after every derived one.
/// </para>
/// <para>
/// The registry is process-wide, so each parameter value adds its filler entries to those already there; that only
/// strengthens the comparison, since the table's lookup must not notice.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[MarkdownExporter]
public class StreamIdTypeTableBenchmarks {
  [Params(16, 1024)]
  public int MessageTypes { get; set; }

  private sealed class Message;
  private sealed class Unregistered;

  private readonly Message _message = new();
  private Type[] _chain = null!;

  [GlobalSetup]
  public void Setup() {
    var fillers = typeof(string).Assembly.GetExportedTypes()
      .Where(t => !t.IsInterface && !t.IsGenericTypeDefinition)
      .Take(MessageTypes - 1)
      .ToArray();
    StreamIdTypeRegistry.Register([.. fillers.Select(t => new StreamIdTypeEntry(t, _ => null, typeof(Guid)))]);
    StreamIdTypeRegistry.Register(new StreamIdTypeEntry(typeof(Message), _ => Guid.Empty, typeof(Guid)));
    _chain = [.. fillers, typeof(Message)];
    _ = StreamIdTypeRegistry.Resolve(typeof(Unregistered));
  }

  /// <summary>The table: one lookup on the message's exact type.</summary>
  [Benchmark(Baseline = true)]
  public StreamIdTypeEntry? Table_ExactType() => StreamIdTypeRegistry.Resolve(_message.GetType());

  /// <summary>The table, for a type with no entry: the cached answer of its base-type walk.</summary>
  [Benchmark]
  public StreamIdTypeEntry? Table_CachedBaseWalk() => StreamIdTypeRegistry.Resolve(typeof(Unregistered));

  /// <summary>The chain of type checks the table replaces, its answer last.</summary>
  [Benchmark]
  public Type? TypeCheckChain() => Array.Find(_chain, type => type.IsInstanceOfType(_message));
}
