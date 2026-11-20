// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Registry;

/// <summary>
/// Pins where the per-type table sits among registered extractors (#1905, #1908): an extractor registered ahead of the
/// generated tier (below priority 100, as the saga events' is) answers first; then the table answers for every type it
/// covers, and its answer is final, so an extractor after it cannot answer a covered type with a base type's view; an
/// uncovered type still reaches every extractor.
/// </summary>
/// <remarks>
/// Every extractor here answers only for this class's own types, and every type is this class's own, so nothing it
/// registers changes what another test sees.
/// </remarks>
[Category("Core")]
[Category("Registry")]
[NotInParallel("StreamIdExtractorRegistry")]
public class StreamIdExtractorRegistryTypeTableTests {
  private static readonly Guid _tableId = new("018f2a6e-0000-7000-8000-000000001905");
  private static readonly Guid _extractorId = new("018f2a6e-0000-7000-8000-000000001908");

  /// <summary>Answers <paramref name="answer"/> for instances of <typeparamref name="T"/>, and nothing for any other.</summary>
  private sealed class OnlyFor<T>(Guid answer, bool generate = false) : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => message is T ? answer : null;
    public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) => message is T && generate ? (true, true) : (false, false);
    public Type? GetStreamIdType(Type messageType) => typeof(T).IsAssignableFrom(messageType) ? typeof(string) : null;
    public bool SetStreamId(object message, Guid streamId) => message is T;
  }

  public class Covered;

  /// <summary>A covered type's answer comes from the table, not from an extractor in the generated tier or after it.</summary>
  [Test]
  public async Task ACoveredType_IsAnsweredByTheTableAheadOfTheGeneratedTierAsync() {
    StreamIdExtractorRegistry.Register(new OnlyFor<Covered>(_extractorId, generate: true), priority: 100);
    StreamIdTypeRegistry.Register(new StreamIdTypeEntry(typeof(Covered), _ => _tableId, typeof(Guid)));
    var message = new Covered();

    await Assert.That(StreamIdExtractorRegistry.ExtractStreamId(message, typeof(Covered))).IsEqualTo(_tableId);
    await Assert.That(StreamIdExtractorRegistry.GetGenerationPolicy(message)).IsEqualTo((false, false));
    await Assert.That(StreamIdExtractorRegistry.GetStreamIdType(typeof(Covered))).IsEqualTo(typeof(Guid));
    await Assert.That(StreamIdExtractorRegistry.SetStreamId(message, _tableId)).IsFalse()
      .Because("the table's entry has no setter, and its answer is final");
  }

  public class CoveredWithNoStreamId;

  /// <summary>An entry whose stream id is empty is still the answer: a later extractor does not supply one.</summary>
  [Test]
  public async Task ACoveredTypeWithNoValue_IsNotAnsweredByALaterExtractorAsync() {
    StreamIdExtractorRegistry.Register(new OnlyFor<CoveredWithNoStreamId>(_extractorId), priority: 1000);
    StreamIdTypeRegistry.Register(new StreamIdTypeEntry(typeof(CoveredWithNoStreamId), _ => null, typeof(Guid)));

    await Assert.That(StreamIdExtractorRegistry.ExtractStreamId(new CoveredWithNoStreamId(), typeof(CoveredWithNoStreamId))).IsNull();
  }

  public class Ahead;

  /// <summary>An extractor registered ahead of the generated tier answers before the table, as the saga events' does.</summary>
  [Test]
  public async Task AnExtractorAheadOfTheGeneratedTier_AnswersBeforeTheTableAsync() {
    StreamIdExtractorRegistry.Register(new OnlyFor<Ahead>(_extractorId, generate: true), priority: 50);
    StreamIdTypeRegistry.Register(new StreamIdTypeEntry(typeof(Ahead), _ => _tableId, typeof(Guid),
      set: (_, _, _) => true));
    var message = new Ahead();

    await Assert.That(StreamIdExtractorRegistry.ExtractStreamId(message, typeof(Ahead))).IsEqualTo(_extractorId);
    await Assert.That(StreamIdExtractorRegistry.GetGenerationPolicy(message)).IsEqualTo((true, true));
    await Assert.That(StreamIdExtractorRegistry.GetStreamIdType(typeof(Ahead))).IsEqualTo(typeof(string));
    await Assert.That(StreamIdExtractorRegistry.SetStreamId(message, _tableId)).IsTrue();
  }

  public class AheadButSilentBase;
  public class AheadButSilent : AheadButSilentBase;

  /// <summary>An extractor ahead of the tier that does not know a type leaves it to the table.</summary>
  [Test]
  public async Task AnExtractorAheadOfTheTierThatDoesNotAnswer_LeavesTheTypeToTheTableAsync() {
    StreamIdExtractorRegistry.Register(new OnlyFor<Unrelated>(_extractorId), priority: 50);
    StreamIdTypeRegistry.Register(new StreamIdTypeEntry(typeof(AheadButSilentBase), _ => _tableId, typeof(string),
      shouldGenerate: true, set: (_, id, source) => id == _tableId && source == typeof(Guid)));
    var message = new AheadButSilent();

    await Assert.That(StreamIdExtractorRegistry.ExtractStreamId(message, typeof(AheadButSilent))).IsEqualTo(_tableId);
    await Assert.That(StreamIdExtractorRegistry.GetGenerationPolicy(message)).IsEqualTo((true, false));
    await Assert.That(StreamIdExtractorRegistry.GetStreamIdType(typeof(AheadButSilent))).IsEqualTo(typeof(string));
    await Assert.That(StreamIdExtractorRegistry.SetStreamId(message, _tableId, typeof(Guid))).IsTrue();
  }

  public class Unrelated;

  /// <summary>A type the table does not cover still reaches the extractors after the generated tier.</summary>
  [Test]
  public async Task AnUncoveredType_ReachesTheExtractorsAfterTheTierAsync() {
    StreamIdExtractorRegistry.Register(new OnlyFor<Unrelated>(_extractorId, generate: true), priority: 1000);
    var message = new Unrelated();

    await Assert.That(StreamIdExtractorRegistry.ExtractStreamId(message, typeof(Unrelated))).IsEqualTo(_extractorId);
    await Assert.That(StreamIdExtractorRegistry.GetGenerationPolicy(message)).IsEqualTo((true, true));
    await Assert.That(StreamIdExtractorRegistry.GetStreamIdType(typeof(Unrelated))).IsEqualTo(typeof(string));
    await Assert.That(StreamIdExtractorRegistry.SetStreamId(message, _extractorId)).IsTrue();
  }
}
