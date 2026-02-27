using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="SyncFilter"/>, <see cref="SyncFilterBuilder"/>, and <see cref="SyncFilterNode"/> types.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class SyncFilterBuilderTests {
  // ==========================================================================
  // SyncFilterNode record tests
  // ==========================================================================

  [Test]
  public async Task StreamFilter_StoresStreamIdAsync() {
    var streamId = Guid.NewGuid();
    var filter = new StreamFilter(streamId);

    await Assert.That(filter.StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task EventTypeFilter_StoresEventTypesAsync() {
    var eventTypes = new[] { typeof(string), typeof(int) };
    var filter = new EventTypeFilter(eventTypes);

    await Assert.That(filter.EventTypes.Count).IsEqualTo(2);
    await Assert.That(filter.EventTypes).Contains(typeof(string));
    await Assert.That(filter.EventTypes).Contains(typeof(int));
  }

  [Test]
  public async Task CurrentScopeFilter_CanBeCreatedAsync() {
    var filter = new CurrentScopeFilter();

    await Assert.That(filter).IsNotNull();
  }

  [Test]
  public async Task AllPendingFilter_CanBeCreatedAsync() {
    var filter = new AllPendingFilter();

    await Assert.That(filter).IsNotNull();
  }

  [Test]
  public async Task AndFilter_StoresLeftAndRightAsync() {
    var left = new CurrentScopeFilter();
    var right = new AllPendingFilter();
    var filter = new AndFilter(left, right);

    await Assert.That(filter.Left).IsEqualTo(left);
    await Assert.That(filter.Right).IsEqualTo(right);
  }

  [Test]
  public async Task OrFilter_StoresLeftAndRightAsync() {
    var left = new CurrentScopeFilter();
    var right = new AllPendingFilter();
    var filter = new OrFilter(left, right);

    await Assert.That(filter.Left).IsEqualTo(left);
    await Assert.That(filter.Right).IsEqualTo(right);
  }

  // ==========================================================================
  // SyncFilter static entry points tests
  // ==========================================================================

  [Test]
  public async Task SyncFilter_ForStream_CreatesBuilderWithStreamFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId);
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<StreamFilter>();
    var streamFilter = (StreamFilter)options.Filter;
    await Assert.That(streamFilter.StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes).Contains(typeof(string));
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_MultipleGeneric_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes).Contains(typeof(string));
    await Assert.That(typeFilter.EventTypes).Contains(typeof(int));
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_3Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(3);
    await Assert.That(typeFilter.EventTypes).Contains(typeof(string));
    await Assert.That(typeFilter.EventTypes).Contains(typeof(int));
    await Assert.That(typeFilter.EventTypes).Contains(typeof(double));
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_4Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double, float>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(4);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_5Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double, float, decimal>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(5);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_6Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double, float, decimal, long>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(6);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_7Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double, float, decimal, long, short>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(7);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_8Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double, float, decimal, long, short, byte>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(8);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_9Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double, float, decimal, long, short, byte, char>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(9);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_10Generic_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes<string, int, double, float, decimal, long, short, byte, char, bool>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(10);
  }

  [Test]
  public async Task SyncFilter_ForEventTypes_Params_CreatesBuilderAsync() {
    var builder = SyncFilter.ForEventTypes(typeof(string), typeof(int));
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)options.Filter;
    await Assert.That(typeFilter.EventTypes).Contains(typeof(string));
    await Assert.That(typeFilter.EventTypes).Contains(typeof(int));
  }

  [Test]
  public async Task SyncFilter_CurrentScope_CreatesBuilderAsync() {
    var builder = SyncFilter.CurrentScope();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<CurrentScopeFilter>();
  }

  [Test]
  public async Task SyncFilter_All_CreatesBuilderAsync() {
    var builder = SyncFilter.All();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AllPendingFilter>();
  }

  // ==========================================================================
  // SyncFilterBuilder AND combinator tests
  // ==========================================================================

  [Test]
  public async Task SyncFilterBuilder_And_CombinesFiltersAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .And(SyncFilter.CurrentScope());
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    await Assert.That(andFilter.Left).IsTypeOf<StreamFilter>();
    await Assert.That(andFilter.Right).IsTypeOf<CurrentScopeFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_AndStream_AddsStreamFilterAsync() {
    var streamId1 = Guid.NewGuid();
    var streamId2 = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId1)
        .AndStream(streamId2);
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    await Assert.That(andFilter.Right).IsTypeOf<EventTypeFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_3Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    await Assert.That(andFilter.Right).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(3);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_4Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double, float>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(4);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_5Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double, float, decimal>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(5);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_6Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double, float, decimal, long>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(6);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_7Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double, float, decimal, long, short>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(7);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_8Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double, float, decimal, long, short, byte>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(8);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_9Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double, float, decimal, long, short, byte, char>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(9);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_10Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string, int, double, float, decimal, long, short, byte, char, bool>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    var typeFilter = (EventTypeFilter)andFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(10);
  }

  [Test]
  public async Task SyncFilterBuilder_AndEventTypes_Params_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes(typeof(string), typeof(int));
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    await Assert.That(andFilter.Right).IsTypeOf<EventTypeFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_AndCurrentScope_AddsCurrentScopeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .AndCurrentScope();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var andFilter = (AndFilter)options.Filter;
    await Assert.That(andFilter.Right).IsTypeOf<CurrentScopeFilter>();
  }

  // ==========================================================================
  // SyncFilterBuilder OR combinator tests
  // ==========================================================================

  [Test]
  public async Task SyncFilterBuilder_Or_CombinesFiltersAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .Or(SyncFilter.CurrentScope());
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    await Assert.That(orFilter.Left).IsTypeOf<StreamFilter>();
    await Assert.That(orFilter.Right).IsTypeOf<CurrentScopeFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_OrStream_AddsStreamFilterAsync() {
    var streamId1 = Guid.NewGuid();
    var streamId2 = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId1)
        .OrStream(streamId2);
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    await Assert.That(orFilter.Right).IsTypeOf<EventTypeFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_3Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    await Assert.That(orFilter.Right).IsTypeOf<EventTypeFilter>();
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(3);
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_4Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double, float>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(4);
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_5Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double, float, decimal>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(5);
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_6Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double, float, decimal, long>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(6);
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_7Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double, float, decimal, long, short>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(7);
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_8Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double, float, decimal, long, short, byte>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(8);
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_9Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double, float, decimal, long, short, byte, char>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(9);
  }

  [Test]
  public async Task SyncFilterBuilder_OrEventTypes_10Generic_AddsEventTypeFilterAsync() {
    var streamId = Guid.NewGuid();
    var builder = SyncFilter.ForStream(streamId)
        .OrEventTypes<string, int, double, float, decimal, long, short, byte, char, bool>();
    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    var typeFilter = (EventTypeFilter)orFilter.Right;
    await Assert.That(typeFilter.EventTypes.Count).IsEqualTo(10);
  }

  // ==========================================================================
  // PerspectiveSyncOptions tests
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncOptions_DefaultTimeout_Is5SecondsAsync() {
    var options = new PerspectiveSyncOptions {
      Filter = new CurrentScopeFilter()
    };

    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.FromSeconds(5));
  }

  [Test]
  public async Task PerspectiveSyncOptions_DefaultDebuggerAwareTimeout_IsTrueAsync() {
    var options = new PerspectiveSyncOptions {
      Filter = new CurrentScopeFilter()
    };

    await Assert.That(options.DebuggerAwareTimeout).IsTrue();
  }

  // ==========================================================================
  // Timeout configuration tests
  // ==========================================================================

  [Test]
  public async Task SyncFilterBuilder_WithTimeout_SetsTimeoutAsync() {
    var timeout = TimeSpan.FromSeconds(10);
    var builder = SyncFilter.CurrentScope().WithTimeout(timeout);
    var options = builder.Build();

    await Assert.That(options.Timeout).IsEqualTo(timeout);
  }

  // ==========================================================================
  // Implicit conversion tests
  // ==========================================================================

  [Test]
  public async Task SyncFilterBuilder_ImplicitConversion_ToPerspectiveSyncOptionsAsync() {
    var builder = SyncFilter.CurrentScope();
    PerspectiveSyncOptions options = builder;

    await Assert.That(options).IsNotNull();
    await Assert.That(options.Filter).IsTypeOf<CurrentScopeFilter>();
  }

  // ==========================================================================
  // Complex filter combination tests
  // ==========================================================================

  [Test]
  public async Task SyncFilterBuilder_ComplexAndOrCombination_WorksAsync() {
    var streamId = Guid.NewGuid();

    // (StreamFilter AND EventTypeFilter) OR CurrentScopeFilter
    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string>()
        .Or(SyncFilter.CurrentScope());

    var options = builder.Build();

    await Assert.That(options.Filter).IsTypeOf<OrFilter>();
    var orFilter = (OrFilter)options.Filter;
    await Assert.That(orFilter.Left).IsTypeOf<AndFilter>();
    await Assert.That(orFilter.Right).IsTypeOf<CurrentScopeFilter>();
  }

  [Test]
  public async Task SyncFilterBuilder_ChainedAnd_CreatesNestedAndFiltersAsync() {
    var streamId = Guid.NewGuid();

    var builder = SyncFilter.ForStream(streamId)
        .AndEventTypes<string>()
        .AndCurrentScope();

    var options = builder.Build();

    // Should be: AndFilter(AndFilter(StreamFilter, EventTypeFilter), CurrentScopeFilter)
    await Assert.That(options.Filter).IsTypeOf<AndFilter>();
    var outerAnd = (AndFilter)options.Filter;
    await Assert.That(outerAnd.Left).IsTypeOf<AndFilter>();
    await Assert.That(outerAnd.Right).IsTypeOf<CurrentScopeFilter>();
  }
}
