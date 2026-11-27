// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2588: integrity exclusions are bound from <c>Whizbang:StreamIntegrity:Exclusions</c> with documented defaults, and a
/// bound below its floor fails host start naming the key.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/IntegrityExclusions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIntegrityOptionsValidator.cs</code-under-test>
public class IntegrityExclusionOptionsTests {
  private const string SECTION = "Whizbang:StreamIntegrity:Exclusions:";
  private const string EXCLUDED_GAUGE = "whizbang.stream_integrity.excluded_events";
  private const string PENDING_GAUGE = "whizbang.stream_integrity.exclusions_pending";

  [Test]
  public async Task Exclusions_HaveTheDocumentedDefaultsAsync() {
    var exclusions = new StreamIntegrityOptions().Exclusions;

    await Assert.That(exclusions.BatchSize).IsEqualTo(1000);
    await Assert.That(exclusions.InlineLimit).IsEqualTo(100);
    await Assert.That(exclusions.LogRetention).IsEqualTo(TimeSpan.FromDays(365));
  }

  [Test]
  public async Task Exclusions_BindFromTheirSectionAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      [SECTION + "BatchSize"] = "50",
      [SECTION + "InlineLimit"] = "0",
      [SECTION + "LogRetention"] = "30.00:00:00",
    }).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var exclusions = provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value.Exclusions;

    await Assert.That(exclusions.BatchSize).IsEqualTo(50);
    await Assert.That(exclusions.InlineLimit).IsEqualTo(0);
    await Assert.That(exclusions.LogRetention).IsEqualTo(TimeSpan.FromDays(30));
  }

  [Test]
  public async Task Exclusions_BoundsBelowTheirFloor_FailNamingTheKeyAsync() {
    var options = new StreamIntegrityOptions {
      Exclusions = { BatchSize = 0, InlineLimit = -1, LogRetention = TimeSpan.Zero },
    };

    var result = new StreamIntegrityOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains(SECTION + "BatchSize");
    await Assert.That(result.FailureMessage).Contains(SECTION + "InlineLimit");
    await Assert.That(result.FailureMessage).Contains(SECTION + "LogRetention");
  }

  [Test]
  public async Task Exclusions_AtTheirFloor_AreValidAsync() {
    var options = new StreamIntegrityOptions {
      Exclusions = { BatchSize = 1, InlineLimit = 0, LogRetention = TimeSpan.FromTicks(1) },
    };

    var result = new StreamIntegrityOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsFalse();
  }

  [Test]
  public async Task TheExclusionGauges_PublishEachOrigin_AndANullReadingPublishesNoneAsync() {
    await using var meters = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddMetrics().BuildServiceProvider();
    var factory = meters.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>();
    var metrics = new Whizbang.Core.Observability.StreamIntegrityMetrics(new Whizbang.Core.Observability.WhizbangMetrics(meterFactory: factory));
    var excluded = new List<long>();
    var pending = new List<long>();
    using var listener = new System.Diagnostics.Metrics.MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter.Scope == factory && instrument.Name is EXCLUDED_GAUGE or PENDING_GAUGE) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
      (instrument.Name == PENDING_GAUGE ? pending : excluded).Add(value));
    listener.Start();

    metrics.UpdateExclusions([(Guid.NewGuid(), 5, 2)]);
    listener.RecordObservableInstruments();
    metrics.UpdateExclusions(null);
    listener.RecordObservableInstruments();

    await Assert.That(excluded).IsEquivalentTo([5L]).Because("a null reading resets to no series");
    await Assert.That(pending).IsEquivalentTo([2L]);
  }
}
