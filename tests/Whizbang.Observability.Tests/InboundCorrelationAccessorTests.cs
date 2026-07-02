using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// A correlation id captured at an inbound edge must be adopted by the first root dispatch so a
/// client-supplied token (e.g. an <c>X-Correlation-ID</c> header) flows through the whole message graph,
/// while dispatches with no inbound correlation keep minting fresh ids.
/// </summary>
[Category("Observability")]
public class InboundCorrelationAccessorTests {

  [Test]
  public async Task NewRootWithAmbientSecurity_WithInboundCorrelation_AdoptsItAsync() {
    var inbound = CorrelationId.New();
    InboundCorrelationAccessor.Current = inbound;
    try {
      var context = CascadeContext.NewRootWithAmbientSecurity();

      await Assert.That(context.CorrelationId).IsEqualTo(inbound)
        .Because("A correlation id captured at the edge must be adopted by the first root dispatch.");
    } finally {
      InboundCorrelationAccessor.Current = null;
    }
  }

  [Test]
  public async Task NewRootWithAmbientSecurity_WithoutInboundCorrelation_GeneratesFreshAsync() {
    InboundCorrelationAccessor.Current = null;

    var a = CascadeContext.NewRootWithAmbientSecurity();
    var b = CascadeContext.NewRootWithAmbientSecurity();

    await Assert.That(a.CorrelationId).IsNotEqualTo(b.CorrelationId)
      .Because("Without an inbound correlation id, each root mints a fresh unique one.");
  }

  [Test]
  public async Task Current_RoundTripsAndDefaultsToNullAsync() {
    await Assert.That(InboundCorrelationAccessor.Current).IsNull();

    var id = CorrelationId.New();
    InboundCorrelationAccessor.Current = id;
    try {
      await Assert.That(InboundCorrelationAccessor.Current).IsEqualTo(id);
    } finally {
      InboundCorrelationAccessor.Current = null;
    }

    await Assert.That(InboundCorrelationAccessor.Current).IsNull();
  }
}
