// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

namespace Whizbang.Core.Registry;

/// <summary>
/// The stream id of any message: hand-written <see cref="IStreamIdExtractor"/> contributions and the generated per-type
/// table (<see cref="StreamIdTypeRegistry"/>), asked in one order.
/// </summary>
/// <remarks>
/// <para>
/// Each assembly's generator registers one entry per message type it compiles with <see cref="StreamIdTypeRegistry"/>,
/// which resolves a message by its exact runtime type (#1905, #1908). Hand-written extractors register here with a
/// priority. The order is: extractors registered below <see cref="GENERATED_TIER"/> (100), such as the saga events',
/// which must win whatever a base type says; then the table, whose answer for a type it covers is final, so no
/// extractor can answer a derived type with its base type's view; then the remaining extractors, for types the table
/// does not cover (one compiled by an older generator that still registers an extractor, say).
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/delivery-receipts</docs>
/// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTests.cs</tests>
public static class StreamIdExtractorRegistry {
  /// <summary>
  /// The priority of the generated tier. An extractor registered below it is asked before the per-type table (as the
  /// saga events' is); the table answers next for every type it covers; an extractor at or above it is asked only for a
  /// type the table does not cover.
  /// </summary>
  internal const int GENERATED_TIER = 100;

  /// <summary>Each registered extractor's priority, so the table can be asked at its place among them.</summary>
  private static readonly ConcurrentDictionary<IStreamIdExtractor, int> _priorities = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// Register an extractor. Called from [ModuleInitializer] in hand-written code; generated code registers per-type
  /// entries with <see cref="StreamIdTypeRegistry"/> instead (#1905).
  /// </summary>
  /// <param name="extractor">The extractor to register</param>
  /// <param name="priority">Lower = tried first. Below <see cref="GENERATED_TIER"/> (100) it is asked ahead of the
  /// generated per-type table; at or above, only for a type the table does not cover.</param>
  public static void Register(IStreamIdExtractor extractor, int priority = 1000) {
    ArgumentNullException.ThrowIfNull(extractor);
    _priorities[extractor] = priority;
    AssemblyRegistry<IStreamIdExtractor>.Register(extractor, priority);
  }

  /// <summary>
  /// Extract stream ID: an extractor ahead of the generated tier, else the per-type table's entry for the message's
  /// runtime type, else the remaining extractors in priority order.
  /// </summary>
  /// <param name="message">The message to extract from</param>
  /// <param name="messageType">The type of the message</param>
  /// <returns>The stream ID if found, otherwise null</returns>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTypeTableTests.cs</tests>
  public static Guid? ExtractStreamId(object message, Type messageType) {
    return _first(message.GetType(), (message, messageType),
      static (extractor, s) => extractor.ExtractStreamId(s.message, s.messageType),
      static answer => answer.HasValue,
      static (entry, s) => entry.Extract(s.message),
      null);
  }

  /// <summary>
  /// Get a singleton IStreamIdExtractor that delegates to the registry.
  /// Use this for DI registration.
  /// </summary>
  /// <returns>A composite extractor that tries all registered extractors</returns>
  public static IStreamIdExtractor GetComposite() => _compositeInstance.Value;

  private static readonly Lazy<IStreamIdExtractor> _compositeInstance = new(
      () => new CompositeStreamIdExtractor());

  /// <summary>
  /// Count of registered extractors (for diagnostics/testing).
  /// </summary>
  public static int Count => AssemblyRegistry<IStreamIdExtractor>.Count;

  /// <summary>
  /// Get generation policy: the first extractor ahead of the generated tier that says generate, else the per-type
  /// table's policy for the message's runtime type (which may say "do not generate", #1893), else the first remaining
  /// extractor that says generate, or (false, false).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTypeTableTests.cs</tests>
  public static (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) {
    return _first(message.GetType(), message,
      static (extractor, m) => extractor.GetGenerationPolicy(m),
      static answer => answer.Item1,
      static (entry, _) => (entry.ShouldGenerate, entry.OnlyIfEmpty),
      (false, false));
  }

  /// <summary>
  /// The stream-id type of a message type: an extractor ahead of the generated tier, else the per-type table's entry,
  /// else the first non-null answer of the remaining extractors.
  /// </summary>
  /// <param name="messageType">The runtime type of the message.</param>
  /// <returns>The strong-id type of its stream id, <see cref="Guid"/> for one that is not a strong id, or null when
  /// nothing knows the type.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTests.cs:GetStreamIdType_ReturnsTheFirstTypeAnExtractorReportsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTypeTableTests.cs</tests>
  public static Type? GetStreamIdType(Type messageType) {
    return _first(messageType, messageType,
      static (extractor, type) => extractor.GetStreamIdType(type),
      static answer => answer is not null,
      static (entry, _) => entry.StreamIdType,
      null);
  }

  /// <summary>
  /// Set stream ID: an extractor ahead of the generated tier, else the per-type table's entry for the message's runtime
  /// type, else the remaining extractors. Returns true if the value was set.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTypeTableTests.cs</tests>
  public static bool SetStreamId(object message, Guid streamId) {
    return _first(message.GetType(), (message, streamId),
      static (extractor, s) => extractor.SetStreamId(s.message, s.streamId),
      static answer => answer,
      static (entry, s) => entry.Set(s.message, s.streamId, null),
      false);
  }

  /// <summary>
  /// Set an inherited stream id, passing each the source's stream-id type: an extractor ahead of the generated tier,
  /// else the per-type table's entry, else the remaining extractors. Returns true if the value was set.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTests.cs:SetStreamId_WithASourceType_PassesItToEachExtractorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTypeTableTests.cs</tests>
  public static bool SetStreamId(object message, Guid streamId, Type? sourceStreamIdType) {
    return _first(message.GetType(), (message, streamId, sourceStreamIdType),
      static (extractor, s) => extractor.SetStreamId(s.message, s.streamId, s.sourceStreamIdType),
      static answer => answer,
      static (entry, s) => entry.Set(s.message, s.streamId, s.sourceStreamIdType),
      false);
  }

  /// <summary>
  /// The first answer: from the extractors registered ahead of the generated tier, in priority order; then the per-type
  /// table's entry for <paramref name="type"/>, which is final for a type it covers; then the remaining extractors.
  /// </summary>
  private static TAnswer _first<TState, TAnswer>(
      Type type,
      TState state,
      Func<IStreamIdExtractor, TState, TAnswer> ask,
      Func<TAnswer, bool> answered,
      Func<StreamIdTypeEntry, TState, TAnswer> fromEntry,
      TAnswer none) {
    var tableAsked = false;
    foreach (var extractor in AssemblyRegistry<IStreamIdExtractor>.GetOrderedContributions()) {
      if (!tableAsked && !_isAheadOfTheTable(extractor)) {
        tableAsked = true;
        if (StreamIdTypeRegistry.Resolve(type) is { } entry) {
          return fromEntry(entry, state);
        }
      }
      var answer = ask(extractor, state);
      if (answered(answer)) {
        return answer;
      }
    }
    return !tableAsked && StreamIdTypeRegistry.Resolve(type) is { } last ? fromEntry(last, state) : none;
  }

  /// <summary>
  /// Whether <paramref name="extractor"/> was registered here below the generated tier. One added to the assembly
  /// registry directly has no priority recorded here and is treated as after the table.
  /// </summary>
  private static bool _isAheadOfTheTable(IStreamIdExtractor extractor) =>
    _priorities.GetValueOrDefault(extractor, int.MaxValue) < GENERATED_TIER;

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S3218:Inner class members should not shadow outer class \"static\" members", Justification = "Explicit interface implementations must match interface member names; they are not reachable via the outer type identifier so there is no real shadowing risk.")]
  private sealed class CompositeStreamIdExtractor : IStreamIdExtractor {
    Guid? IStreamIdExtractor.ExtractStreamId(object message, Type messageType) =>
        StreamIdExtractorRegistry.ExtractStreamId(message, messageType);

    (bool ShouldGenerate, bool OnlyIfEmpty) IStreamIdExtractor.GetGenerationPolicy(object message) =>
        StreamIdExtractorRegistry.GetGenerationPolicy(message);

    Type? IStreamIdExtractor.GetStreamIdType(Type messageType) =>
        StreamIdExtractorRegistry.GetStreamIdType(messageType);

    bool IStreamIdExtractor.SetStreamId(object message, Guid streamId) =>
        StreamIdExtractorRegistry.SetStreamId(message, streamId);

    bool IStreamIdExtractor.SetStreamId(object message, Guid streamId, Type? sourceStreamIdType) =>
        StreamIdExtractorRegistry.SetStreamId(message, streamId, sourceStreamIdType);
  }
}
