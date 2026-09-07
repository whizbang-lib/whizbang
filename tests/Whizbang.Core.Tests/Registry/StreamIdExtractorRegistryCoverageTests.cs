using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Registry;

/// <summary>
/// Targeted coverage for <see cref="StreamIdExtractorRegistry.Count"/> — a diagnostic surface the
/// broader <see cref="StreamIdExtractorRegistryTests"/> suite never reads. Shares that suite's
/// process-global static state (<see cref="AssemblyRegistry{T}"/> for
/// <see cref="IStreamIdExtractor"/>), so it uses the same <c>[NotInParallel]</c> key and restores
/// the generated extractors afterward.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Registry/StreamIdExtractorRegistry.cs</code-under-test>
[Category("Core")]
[Category("Registry")]
[NotInParallel("StreamIdExtractorRegistry")]
public class StreamIdExtractorRegistryCoverageTests {

  [After(Test)]
  public Task RestoreRegistryAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(
        new Whizbang.Core.Generated.GeneratedStreamIdExtractor(), priority: 100);
    StreamIdExtractorRegistry.Register(
        new Whizbang.Core.Tests.Generated.GeneratedStreamIdExtractor(), priority: 100);
    return Task.CompletedTask;
  }

  [Test]
  public async Task Count_ReflectsTheNumberOfRegisteredExtractorsAsync() {
    // Count exists so an operator debugging "why isn't my stream id being generated" can confirm
    // whether ANY extractor is registered at all — if this drifted from the real registration
    // count, that diagnostic would mislead exactly the person trying to use it.
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();
    StreamIdExtractorRegistry.Register(new _noopExtractor());
    StreamIdExtractorRegistry.Register(new _noopExtractor());
    StreamIdExtractorRegistry.Register(new _noopExtractor());

    await Assert.That(StreamIdExtractorRegistry.Count).IsEqualTo(3);
  }

  [Test]
  public async Task Count_WithNoExtractorsRegistered_IsZeroAsync() {
    AssemblyRegistry<IStreamIdExtractor>.ClearForTesting();

    await Assert.That(StreamIdExtractorRegistry.Count).IsEqualTo(0);
  }

  private sealed class _noopExtractor : IStreamIdExtractor {
    public Guid? ExtractStreamId(object message, Type messageType) => null;
    public (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) => (false, false);
    public bool SetStreamId(object message, Guid streamId) => false;
  }
}
