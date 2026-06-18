#pragma warning disable CA1707

using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Locks the <see cref="EventCategoryMetrics"/> shape and recording
/// behavior. The class is the cross-cutting metrics seam for
/// non-regular event categories (collective + composite); changing the
/// instrument names, meter name, or tag keys would break consumer
/// dashboards.
/// </summary>
/// <docs>operations/observability/metrics</docs>
[Category("Core")]
[Category("Observability")]
public class EventCategoryMetricsTests {

  // ── Identity ───────────────────────────────────────────────────────────

  [Test]
  public async Task EventCategoryMetrics_MeterName_IsWhizbangEventCategoriesAsync() {
    var meterName = EventCategoryMetrics.METER_NAME;
    await Assert.That(meterName).IsEqualTo("Whizbang.EventCategories")
      .Because("Renaming the meter changes the OTLP export. Downstream dashboards / Grafana scrape configs depend on the value.");
  }

  // ── Constructor wires every instrument ────────────────────────────────

  [Test]
  public async Task EventCategoryMetrics_Constructor_CreatesAllInstrumentsAsync() {
    var metrics = new EventCategoryMetrics(new WhizbangMetrics());

    await Assert.That(metrics.DispatchDuration).IsNotNull();
    await Assert.That(metrics.Fanout).IsNotNull();
    await Assert.That(metrics.Dispatched).IsNotNull();
    await Assert.That(metrics.Errors).IsNotNull();
  }

  [Test]
  public async Task EventCategoryMetrics_NullFactory_ThrowsArgumentNullAsync() {
    await Assert.That(() => new EventCategoryMetrics(null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  // ── Tag-key constants (drift protection) ──────────────────────────────

  [Test]
  public async Task EventCategoryMetrics_TagKeys_AreStableAcrossReleasesAsync() {
    var category = EventCategoryMetrics.Tags.CATEGORY;
    var eventType = EventCategoryMetrics.Tags.EVENT_TYPE;
    var eventNamespace = EventCategoryMetrics.Tags.EVENT_NAMESPACE;
    var scopeKind = EventCategoryMetrics.Tags.SCOPE_KIND;
    var modelType = EventCategoryMetrics.Tags.MODEL_TYPE;
    var errorClass = EventCategoryMetrics.Tags.ERROR_CLASS;

    await Assert.That(category).IsEqualTo("category");
    await Assert.That(eventType).IsEqualTo("event_type");
    await Assert.That(eventNamespace).IsEqualTo("event_namespace");
    await Assert.That(scopeKind).IsEqualTo("scope_kind");
    await Assert.That(modelType).IsEqualTo("model_type");
    await Assert.That(errorClass).IsEqualTo("error_class")
      .Because("Tag keys are part of the OTLP contract. Renaming requires coordinated downstream dashboard changes.");
  }

  [Test]
  public async Task EventCategoryMetrics_CategoryValues_AreStableAcrossReleasesAsync() {
    var collective = EventCategoryMetrics.Categories.COLLECTIVE;
    var composite = EventCategoryMetrics.Categories.COMPOSITE;
    await Assert.That(collective).IsEqualTo("collective");
    await Assert.That(composite).IsEqualTo("composite");
  }

  [Test]
  public async Task EventCategoryMetrics_ErrorClassValues_AreStableAcrossReleasesAsync() {
    var resolverMissing = EventCategoryMetrics.ErrorClasses.RESOLVER_MISSING;
    var executorMissing = EventCategoryMetrics.ErrorClasses.EXECUTOR_MISSING;
    var expansionExceeded = EventCategoryMetrics.ErrorClasses.EXPANSION_LIMIT_EXCEEDED;
    var sqlException = EventCategoryMetrics.ErrorClasses.SQL_EXCEPTION;
    var unknown = EventCategoryMetrics.ErrorClasses.UNKNOWN;
    await Assert.That(resolverMissing).IsEqualTo("resolver_missing");
    await Assert.That(executorMissing).IsEqualTo("executor_missing");
    await Assert.That(expansionExceeded).IsEqualTo("expansion_limit_exceeded");
    await Assert.That(sqlException).IsEqualTo("sql_exception");
    await Assert.That(unknown).IsEqualTo("unknown");
  }

  // ── Recording roundtrip ───────────────────────────────────────────────

  [Test]
  public async Task EventCategoryMetrics_Dispatched_RecordedWithTagsAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new EventCategoryMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(EventCategoryMetrics.METER_NAME);

    metrics.Dispatched.Add(1,
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.EVENT_TYPE, "MyApp.ArchiveJobsCollectiveEvent"),
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.SCOPE_KIND, "tenant"));

    var measurements = helper.GetByName("whizbang.event_category.dispatched");
    await Assert.That(measurements).IsNotEmpty();
    await Assert.That(measurements[0].Value).IsEqualTo(1.0);
    await Assert.That(measurements[0].Tags["category"]).IsEqualTo("collective");
    await Assert.That(measurements[0].Tags["event_type"]).IsEqualTo("MyApp.ArchiveJobsCollectiveEvent");
    await Assert.That(measurements[0].Tags["scope_kind"]).IsEqualTo("tenant");
  }

  [Test]
  public async Task EventCategoryMetrics_Fanout_RecordedWithModelTypeTagAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new EventCategoryMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(EventCategoryMetrics.METER_NAME);

    metrics.Fanout.Record(42,
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.MODEL_TYPE, "JobModel"));

    var measurements = helper.GetByName("whizbang.event_category.fanout");
    await Assert.That(measurements).IsNotEmpty();
    await Assert.That(measurements[0].Value).IsEqualTo(42.0);
    await Assert.That(measurements[0].Tags["model_type"]).IsEqualTo("JobModel");
  }

  [Test]
  public async Task EventCategoryMetrics_Errors_RecordedWithErrorClassTagAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new EventCategoryMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(EventCategoryMetrics.METER_NAME);

    metrics.Errors.Add(1,
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.CATEGORY, EventCategoryMetrics.Categories.COLLECTIVE),
      new KeyValuePair<string, object?>(EventCategoryMetrics.Tags.ERROR_CLASS, EventCategoryMetrics.ErrorClasses.RESOLVER_MISSING));

    var measurements = helper.GetByName("whizbang.event_category.errors");
    await Assert.That(measurements).IsNotEmpty();
    await Assert.That(measurements[0].Tags["error_class"]).IsEqualTo("resolver_missing");
  }
}
