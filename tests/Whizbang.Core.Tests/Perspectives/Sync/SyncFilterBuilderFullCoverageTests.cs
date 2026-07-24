using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Additional coverage tests for <see cref="SyncFilterBuilder"/> targeting remaining uncovered branches.
/// Focuses on: verifying all generic overload return values, method chaining correctness for each arity,
/// and composite filter tree structure validation for every overload.
/// </summary>
public class SyncFilterBuilderFullCoverageTests {
  // Dummy types for generic overloads
  private sealed record _typeA;
  private sealed record _typeB;
  private sealed record _typeC;
  private sealed record _typeD;
  private sealed record _typeE;
  private sealed record _typeF;
  private sealed record _typeG;
  private sealed record _typeH;
  private sealed record _typeI;
  private sealed record _typeJ;

  // ==========================================================================
  // AND generic overloads — verify event type count and builder chaining
  // Each overload is distinct code that may be uncovered
  // ==========================================================================

  [Test]
  public async Task AndEventTypes_1Generic_ReturnsBuilderAndContainsCorrectTypeAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(1);
    await Assert.That(typeFilter.EventTypes).Contains(typeof(_typeA));
  }

  [Test]
  public async Task AndEventTypes_2Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(2);
    await Assert.That(typeFilter.EventTypes).Contains(typeof(_typeA));
    await Assert.That(typeFilter.EventTypes).Contains(typeof(_typeB));
  }

  [Test]
  public async Task AndEventTypes_3Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(3);
  }

  [Test]
  public async Task AndEventTypes_4Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC, _typeD>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(4);
  }

  [Test]
  public async Task AndEventTypes_5Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(5);
  }

  [Test]
  public async Task AndEventTypes_6Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(6);
  }

  [Test]
  public async Task AndEventTypes_7Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(7);
  }

  [Test]
  public async Task AndEventTypes_8Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(8);
  }

  [Test]
  public async Task AndEventTypes_9Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH, _typeI>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(9);
  }

  [Test]
  public async Task AndEventTypes_10Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH, _typeI, _typeJ>();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(10);
  }

  // ==========================================================================
  // OR generic overloads — verify event type count
  // ==========================================================================

  [Test]
  public async Task OrEventTypes_1Generic_ContainsCorrectTypeAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(1);
    await Assert.That(typeFilter.EventTypes).Contains(typeof(_typeA));
  }

  [Test]
  public async Task OrEventTypes_2Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(2);
  }

  [Test]
  public async Task OrEventTypes_3Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(3);
  }

  [Test]
  public async Task OrEventTypes_4Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC, _typeD>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(4);
  }

  [Test]
  public async Task OrEventTypes_5Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(5);
  }

  [Test]
  public async Task OrEventTypes_6Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(6);
  }

  [Test]
  public async Task OrEventTypes_7Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(7);
  }

  [Test]
  public async Task OrEventTypes_8Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(8);
  }

  [Test]
  public async Task OrEventTypes_9Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH, _typeI>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(9);
  }

  [Test]
  public async Task OrEventTypes_10Generic_ContainsCorrectTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH, _typeI, _typeJ>();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(10);
  }

  // ==========================================================================
  // SyncFilter static entry points — verify all generic overloads produce correct types
  // Using unique type combinations to ensure each overload is separately invoked
  // ==========================================================================

  [Test]
  public async Task SyncFilter_ForEventTypes_1Generic_WithDummyType_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(1);
    await Assert.That(typeFilter.EventTypes).Contains(typeof(_typeA));
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_2Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(2);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_3Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(3);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_4Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC, _typeD>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(4);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_5Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(5);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_6Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(6);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_7Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(7);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_8Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(8);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_9Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH, _typeI>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(9);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_10Generic_WithDummyTypes_CreatesFilterAsync() {
    var builder = SyncFilter.ForEventTypes<_typeA, _typeB, _typeC, _typeD, _typeE, _typeF, _typeG, _typeH, _typeI, _typeJ>();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(10);
  }

  // ==========================================================================
  // AND/OR params with empty array
  // ==========================================================================

  [Test]
  public async Task AndEventTypes_EmptyParams_CreatesFilterWithNoTypesAsync() {
    var builder = SyncFilter.All().AndEventTypes();
    var options = builder.Build();

    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(0);
  }

  [Test]
  public async Task OrEventTypes_EmptyParams_CreatesFilterWithNoTypesAsync() {
    var builder = SyncFilter.All().OrEventTypes();
    var options = builder.Build();

    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // ForEventTypes params with empty array
  // ==========================================================================

  [Test]
  public async Task SyncFilter_ForEventTypes_EmptyParams_CreatesEmptyFilterAsync() {
    var builder = SyncFilter.ForEventTypes();
    var options = builder.Build();

    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(0);
  }

  // ==========================================================================
  // Complex multi-level chaining with all combinators
  // ==========================================================================

  [Test]
  public async Task ComplexChain_And_Or_WithTimeout_ProducesCorrectTreeAsync() {
    var streamId = Guid.NewGuid();
    var timeout = TimeSpan.FromSeconds(42);

    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<_typeA, _typeB>()
        .OrStream(Guid.NewGuid())
        .AndCurrentScope()
        .WithTimeout(timeout);

    var options = builder.Build();

    await Assert.That(options.Timeout).IsEqualTo(timeout);
    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    await Assert.That(options.DebuggerAwareTimeout).IsTrue();
  }

  // ==========================================================================
  // Implicit conversion returns same as Build
  // ==========================================================================

  [Test]
  public async Task ImplicitConversion_ProducesSameResultAsBuildAsync() {
    var builder = SyncFilter.ForStream(Guid.NewGuid())
        .AndEventTypes<_typeA>()
        .WithTimeout(TimeSpan.FromSeconds(7));

    var built = builder.Build();
    PerspectiveSyncOptions converted = builder;

    await Assert.That(converted.Timeout).IsEqualTo(built.Timeout);
    await Assert.That(converted.DebuggerAwareTimeout).IsEqualTo(built.DebuggerAwareTimeout);
    // Filter references the same object since Build() is called both times
    await Assert.That(converted.Filter).IsTypeOf<AndFilter>();
  }
}
