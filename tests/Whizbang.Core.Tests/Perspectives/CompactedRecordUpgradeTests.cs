using System;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// E3-5 document-style per-record versioning. A <see cref="Compacted"/> origin carries a
/// <see cref="Compacted.SchemaVersion"/> stamp (E3-1a) and there is no event log to rebuild it from, so its
/// model evolves the state-based way: a developer <see cref="IEventUpcaster"/> upgrades a stale record
/// <c>vN → vN+1</c>, applied through the existing <see cref="EventUpcasterPipeline"/> on read (lazy-on-access).
/// This locks that E3-5 REUSES the schema-evolution machinery — no new upgrade mechanism, just the version
/// stamp + the upcaster seam. (A deploy-time batch runner over all Compacted records is an optional follow-up;
/// lazy-on-read via the pipeline covers correctness.)
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class CompactedRecordUpgradeTests {
  // A developer per-record upgrade: v1 model {"balance":N} -> v2 model {"balance":N,"currency":"USD"}.
  // Same event type (Compacted), so SourceTypes/TargetTypes stay empty — it is a re-key/backfill, not a type change.
  private sealed class CompactedV1ToV2Upcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is Compacted { SchemaVersion: < 2 };
    public IEvent Upcast(IEvent storedEvent) {
      var c = (Compacted)storedEvent;
      var balance = c.Model.GetProperty("balance").GetInt32();
      using var upgraded = JsonDocument.Parse($$"""{"balance":{{balance}},"currency":"USD"}""");
      return c with { Model = upgraded.RootElement.Clone(), SchemaVersion = 2 };
    }
  }

  private static Compacted _v1(int balance) {
    using var model = JsonDocument.Parse($$"""{"balance":{{balance}}}""");
    return new Compacted {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "LedgerBalance",
      Model = model.RootElement.Clone(),
      SchemaVersion = 1,
      ThroughVersion = 10,
    };
  }

  [Test]
  public async Task Compacted_StaleRecord_UpgradesV1ToV2ViaPipelineAsync() {
    var pipeline = new EventUpcasterPipeline([new CompactedV1ToV2Upcaster()]);

    var upgraded = pipeline.Apply(_v1(140)) as Compacted;

    await Assert.That(upgraded).IsNotNull();
    await Assert.That(upgraded!.SchemaVersion).IsEqualTo(2)
      .Because("The record is upgraded to the current schema version, document-style — not rebuilt from events.");
    await Assert.That(upgraded.Model.GetProperty("balance").GetInt32()).IsEqualTo(140)
      .Because("The upgrade transforms the model in place — the balance carries forward.");
    await Assert.That(upgraded.Model.GetProperty("currency").GetString()).IsEqualTo("USD")
      .Because("The v1->v2 transform added the new field.");
  }

  [Test]
  public async Task Compacted_AlreadyCurrent_IsNotUpgradedAsync() {
    var pipeline = new EventUpcasterPipeline([new CompactedV1ToV2Upcaster()]);
    using var model = JsonDocument.Parse("""{"balance":50,"currency":"USD"}""");
    var current = new Compacted {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "LedgerBalance",
      Model = model.RootElement.Clone(),
      SchemaVersion = 2,
      ThroughVersion = 5,
    };

    var result = pipeline.Apply(current) as Compacted;

    await Assert.That(result!.SchemaVersion).IsEqualTo(2)
      .Because("A record already at the current version is not re-upgraded (CanUpcast is false) — idempotent.");
  }

  [Test]
  public async Task Compacted_ChainsV1ToV3ThroughSuccessiveUpcastersAsync() {
    // The pipeline re-checks CanUpcast after each step, so vN -> vN+1 -> vN+2 chains without the developer
    // writing a direct v1->v3 transform — the document-style analogue of multi-step upcasting.
    var v2To3 = new _v2ToV3Upcaster();
    var pipeline = new EventUpcasterPipeline([new CompactedV1ToV2Upcaster(), v2To3]);

    var upgraded = pipeline.Apply(_v1(200)) as Compacted;

    await Assert.That(upgraded!.SchemaVersion).IsEqualTo(3);
    await Assert.That(upgraded.Model.GetProperty("currency").GetString()).IsEqualTo("USD");
    await Assert.That(upgraded.Model.GetProperty("openedAt").GetString()).IsEqualTo("1970-01-01")
      .Because("The v2->v3 step ran after v1->v2 — the chain applied both transforms.");
  }

  private sealed class _v2ToV3Upcaster : IEventUpcaster {
    public bool CanUpcast(IEvent storedEvent) => storedEvent is Compacted { SchemaVersion: 2 };
    public IEvent Upcast(IEvent storedEvent) {
      var c = (Compacted)storedEvent;
      var balance = c.Model.GetProperty("balance").GetInt32();
      var currency = c.Model.GetProperty("currency").GetString();
      using var upgraded = JsonDocument.Parse($$"""{"balance":{{balance}},"currency":"{{currency}}","openedAt":"1970-01-01"}""");
      return c with { Model = upgraded.RootElement.Clone(), SchemaVersion = 3 };
    }
  }
}
