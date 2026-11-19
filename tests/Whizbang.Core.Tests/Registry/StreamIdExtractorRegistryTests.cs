// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707

using System;
using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Registry;

/// <summary>
/// Tests for StreamIdExtractorRegistry static methods: GetGenerationPolicy, SetStreamId,
/// and the CompositeStreamIdExtractor delegation.
/// </summary>
/// <remarks>
/// These tests must run sequentially because they modify global static state
/// (AssemblyRegistry for IStreamIdExtractor).
/// </remarks>
[Category("Core")]
[Category("Registry")]
[NotInParallel("StreamIdExtractorRegistry")]
public class StreamIdExtractorRegistryTests {

  /// <summary>
  /// Restores the original generated extractors after each test.
  /// ClearForTesting() removes all registrations including the module-initializer
  /// registered extractors, which breaks other tests that depend on them.
  /// </summary>
  [After(Test)]
  public Task RestoreRegistryAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    // Re-register the generated extractors from both assemblies
    StreamIdExtractorRegistry.Register(
        new Whizbang.Core.Generated.GeneratedStreamIdExtractor(), priority: 100);
    StreamIdExtractorRegistry.Register(
        new Whizbang.Core.Tests.Generated.GeneratedStreamIdExtractor(), priority: 100);
    return Task.CompletedTask;
  }

  [Test]
  public async Task GetGenerationPolicy_WithNoExtractors_ReturnsFalseFalseAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();

    // Act
    var (shouldGenerate, onlyIfEmpty) = StreamIdExtractorRegistry.GetGenerationPolicy(new object());

    // Assert
    await Assert.That(shouldGenerate).IsEqualTo(false);
    await Assert.That(onlyIfEmpty).IsEqualTo(false);
  }

  [Test]
  public async Task GetGenerationPolicy_WithExtractorThatShouldGenerate_ReturnsTrueAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new GeneratingStreamIdExtractor(shouldGenerate: true, onlyIfEmpty: false));

    // Act
    var (shouldGenerate, onlyIfEmpty) = StreamIdExtractorRegistry.GetGenerationPolicy(new object());

    // Assert
    await Assert.That(shouldGenerate).IsEqualTo(true);
    await Assert.That(onlyIfEmpty).IsEqualTo(false);
  }

  [Test]
  public async Task GetGenerationPolicy_WithExtractorShouldGenerateOnlyIfEmpty_ReturnsTrueTrueAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new GeneratingStreamIdExtractor(shouldGenerate: true, onlyIfEmpty: true));

    // Act
    var (shouldGenerate, onlyIfEmpty) = StreamIdExtractorRegistry.GetGenerationPolicy(new object());

    // Assert
    await Assert.That(shouldGenerate).IsEqualTo(true);
    await Assert.That(onlyIfEmpty).IsEqualTo(true);
  }

  [Test]
  public async Task GetGenerationPolicy_WithMultipleExtractors_ReturnsFirstMatchAsync() {
    // Arrange: First extractor says no, second says yes
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new GeneratingStreamIdExtractor(shouldGenerate: false, onlyIfEmpty: false), priority: 100);
    StreamIdExtractorRegistry.Register(new GeneratingStreamIdExtractor(shouldGenerate: true, onlyIfEmpty: true), priority: 200);

    // Act
    var (shouldGenerate, onlyIfEmpty) = StreamIdExtractorRegistry.GetGenerationPolicy(new object());

    // Assert: Second extractor's result because first returned ShouldGenerate=false
    await Assert.That(shouldGenerate).IsEqualTo(true);
    await Assert.That(onlyIfEmpty).IsEqualTo(true);
  }

  [Test]
  public async Task SetStreamId_WithNoExtractors_ReturnsFalseAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();

    // Act
    var result = StreamIdExtractorRegistry.SetStreamId(new object(), Guid.NewGuid());

    // Assert
    await Assert.That(result).IsEqualTo(false);
  }

  [Test]
  public async Task SetStreamId_WithSuccessfulExtractor_ReturnsTrueAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new SettableStreamIdExtractor(canSet: true));

    // Act
    var result = StreamIdExtractorRegistry.SetStreamId(new object(), Guid.NewGuid());

    // Assert
    await Assert.That(result).IsEqualTo(true);
  }

  [Test]
  public async Task SetStreamId_WithFailingThenSucceeding_ReturnsTrueFromSecondAsync() {
    // Arrange: First extractor can't set, second can
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new SettableStreamIdExtractor(canSet: false), priority: 100);
    StreamIdExtractorRegistry.Register(new SettableStreamIdExtractor(canSet: true), priority: 200);

    // Act
    var result = StreamIdExtractorRegistry.SetStreamId(new object(), Guid.NewGuid());

    // Assert
    await Assert.That(result).IsEqualTo(true);
  }

  [Test]
  public async Task SetStreamId_WithAllFailing_ReturnsFalseAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new SettableStreamIdExtractor(canSet: false), priority: 100);
    StreamIdExtractorRegistry.Register(new SettableStreamIdExtractor(canSet: false), priority: 200);

    // Act
    var result = StreamIdExtractorRegistry.SetStreamId(new object(), Guid.NewGuid());

    // Assert
    await Assert.That(result).IsEqualTo(false);
  }

  [Test]
  public async Task GetComposite_DelegatesGetGenerationPolicy_ToRegistryAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new GeneratingStreamIdExtractor(shouldGenerate: true, onlyIfEmpty: true));

    // Act: Use composite (which delegates to static methods)
    var composite = StreamIdExtractorRegistry.GetComposite();
    var (shouldGenerate, onlyIfEmpty) = composite.GetGenerationPolicy(new object());

    // Assert
    await Assert.That(shouldGenerate).IsEqualTo(true);
    await Assert.That(onlyIfEmpty).IsEqualTo(true);
  }

  [Test]
  public async Task GetComposite_DelegatesSetStreamId_ToRegistryAsync() {
    // Arrange
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new SettableStreamIdExtractor(canSet: true));

    // Act: Use composite (which delegates to static methods)
    var composite = StreamIdExtractorRegistry.GetComposite();
    var result = composite.SetStreamId(new object(), Guid.NewGuid());

    // Assert
    await Assert.That(result).IsEqualTo(true);
  }

  // The source's strong-id type travels with the inherited value so an extractor can refuse a stream of another id
  // type (#2072).

  [Test]
  public async Task GetStreamIdType_WithNoExtractors_ReturnsNullAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();

    var type = StreamIdExtractorRegistry.GetStreamIdType(typeof(object));

    await Assert.That(type).IsNull();
  }

  [Test]
  public async Task GetStreamIdType_ReturnsTheFirstTypeAnExtractorReportsAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new SettableStreamIdExtractor(canSet: false), priority: 100);
    StreamIdExtractorRegistry.Register(new TypedStreamIdExtractor(typeof(Uri), accepts: false), priority: 200);
    StreamIdExtractorRegistry.Register(new TypedStreamIdExtractor(typeof(string), accepts: false), priority: 300);

    var type = StreamIdExtractorRegistry.GetStreamIdType(typeof(object));

    await Assert.That(type).IsEqualTo(typeof(Uri))
      .Because("an extractor that knows no type answers null by default, and the next one is asked");
  }

  [Test]
  public async Task SetStreamId_WithASourceType_PassesItToEachExtractorAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    var refusing = new TypedStreamIdExtractor(typeof(Uri), accepts: false);
    var accepting = new TypedStreamIdExtractor(typeof(Uri), accepts: true);
    StreamIdExtractorRegistry.Register(refusing, priority: 100);
    StreamIdExtractorRegistry.Register(accepting, priority: 200);

    var result = StreamIdExtractorRegistry.SetStreamId(new object(), Guid.NewGuid(), typeof(string));

    await Assert.That(result).IsTrue();
    await Assert.That(refusing.SourceTypes.Count == 1 && refusing.SourceTypes[0] == typeof(string)).IsTrue();
    await Assert.That(accepting.SourceTypes.Count == 1 && accepting.SourceTypes[0] == typeof(string)).IsTrue();
  }

  [Test]
  public async Task SetStreamId_WithASourceType_WhenEveryExtractorRefuses_ReturnsFalseAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new TypedStreamIdExtractor(typeof(Uri), accepts: false));

    var result = StreamIdExtractorRegistry.SetStreamId(new object(), Guid.NewGuid(), typeof(string));

    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task SetStreamId_WithASourceType_OnAnExtractorThatKnowsNoTypes_UsesItsPlainSetterAsync() {
    // A custom extractor written before the source type existed keeps working: the default member calls its setter
    IStreamIdExtractor extractor = new SettableStreamIdExtractor(canSet: true);

    var result = extractor.SetStreamId(new object(), Guid.NewGuid(), typeof(string));

    await Assert.That(result).IsTrue();
  }

  [Test]
  public async Task GetComposite_DelegatesGetStreamIdType_ToRegistryAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new TypedStreamIdExtractor(typeof(Uri), accepts: false));

    var composite = StreamIdExtractorRegistry.GetComposite();

    await Assert.That(composite.GetStreamIdType(typeof(object))).IsEqualTo(typeof(Uri));
  }

  [Test]
  public async Task GetComposite_DelegatesSetStreamIdWithASourceType_ToRegistryAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    var accepting = new TypedStreamIdExtractor(typeof(Uri), accepts: true);
    StreamIdExtractorRegistry.Register(accepting);

    var composite = StreamIdExtractorRegistry.GetComposite();
    var result = composite.SetStreamId(new object(), Guid.NewGuid(), typeof(string));

    await Assert.That(result).IsTrue();
    await Assert.That(accepting.SourceTypes.Count == 1 && accepting.SourceTypes[0] == typeof(string)).IsTrue();
  }

  #region Test Doubles

  /// <summary>Reports one stream-id type for every message, and accepts or refuses every write, recording each source type.</summary>
  private sealed class TypedStreamIdExtractor(Type streamIdType, bool accepts) : IStreamIdExtractor {
    public List<Type?> SourceTypes { get; } = [];

    public Guid? ExtractStreamId(object message, Type messageType) => null;

    public Type? GetStreamIdType(Type messageType) => streamIdType;

    public bool SetStreamId(object message, Guid streamId) => SetStreamId(message, streamId, null);

    public bool SetStreamId(object message, Guid streamId, Type? sourceStreamIdType) {
      SourceTypes.Add(sourceStreamIdType);
      return accepts;
    }
  }

  /// <summary>
  /// Test extractor that returns a configurable generation policy.
  /// </summary>
  private sealed class GeneratingStreamIdExtractor(bool shouldGenerate, bool onlyIfEmpty) : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => null;

    public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) =>
      (shouldGenerate, onlyIfEmpty);

    public bool SetStreamId(object message, Guid streamId) => false;
  }

  /// <summary>
  /// Test extractor that returns a configurable SetStreamId result.
  /// </summary>
  private sealed class SettableStreamIdExtractor(bool canSet) : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => null;

    public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) => (false, false);

    public bool SetStreamId(object message, Guid streamId) => canSet;
  }

  #endregion
}
