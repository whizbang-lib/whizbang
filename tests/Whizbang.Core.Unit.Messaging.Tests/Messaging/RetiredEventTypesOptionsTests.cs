// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2329: a deleted event type is retired by naming it under <c>Whizbang:EventStore:RetiredTypes</c>. The names are
/// read without their assembly; a blank name or a batch limit below one stops the host at startup, and a name this
/// service still declares as an event type is left out of what it retires.
/// </summary>
[Category("Messaging")]
public class RetiredEventTypesOptionsTests {
  public sealed record StillHere([property: StreamId] Guid Id) : IEvent;

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  [Test]
  public async Task Defaults_RetireNothing_InBatchesOfFiveHundredStreams_TenBatchesACycleAsync() {
    var options = new RetiredEventTypesOptions();

    await Assert.That(options.Names).IsEmpty();
    await Assert.That(options.RetiredNames()).IsEmpty();
    await Assert.That(options.BatchStreams).IsEqualTo(500);
    await Assert.That(options.MaxBatchesPerCycle).IsEqualTo(10);
    await Assert.That(RetiredEventTypesOptions.SECTION).IsEqualTo("Whizbang:EventStore:RetiredTypes");
  }

  [Test]
  public async Task RetiredNames_AreTheNamesWithoutTheirAssembly_OnceEach_InOrdinalOrderAsync() {
    var options = new RetiredEventTypesOptions {
      Names = { "Contracts.Zeta, Contracts", "global::Contracts.Alpha", "Contracts.Zeta", " Contracts.Outer+Nested ", " " },
    };

    await Assert.That(options.RetiredNames()).IsEquivalentTo(["Contracts.Alpha", "Contracts.Outer+Nested", "Contracts.Zeta"]);
    await Assert.That(options.IsRetired("Contracts.Alpha, Other.Assembly")).IsTrue()
      .Because("a stored or wire name carries an assembly; the type is matched by its name alone");
    await Assert.That(options.IsRetired("Contracts.Beta")).IsFalse();
  }

  [Test]
  public async Task Validate_TheDefaults_AndAnyRetiredName_PassAsync() {
    await Assert.That(RetiredEventTypesOptions.Validate(new RetiredEventTypesOptions())).IsEmpty();
    await Assert.That(RetiredEventTypesOptions.Validate(new RetiredEventTypesOptions {
      Names = { "Contracts.Deleted", TypeNameFormatter.Format(typeof(StillHere)) },
    })).IsEmpty().Because("owner decision (2026-11-20): a name this service still declares warns, it never stops the host");
  }

  /// <summary>
  /// Owner decision (2026-11-20): a retired name this service still declares as an event type is not retired here. It is
  /// left out of the effective names every consumer of the setting reads (the purge, the receive gate, the fingerprint),
  /// so a running service never purges, or turns away, events its own readers still apply; it is reported instead.
  /// </summary>
  [Test]
  public async Task EffectiveNames_LeaveOutANameThisServiceStillDeclares_AndStillDeclaredNamesItAsync() {
    var declared = TypeNameFormatter.FormatClrTypeName(typeof(StillHere));
    var options = new RetiredEventTypesOptions { Names = { TypeNameFormatter.Format(typeof(StillHere)), "Contracts.Deleted" } };
    var catalog = new Catalog(_event(typeof(StillHere)));

    await Assert.That(options.EffectiveNames(catalog)).IsEquivalentTo(["Contracts.Deleted"]);
    await Assert.That(options.StillDeclared(catalog)).IsEquivalentTo([declared]);
    await Assert.That(options.IsRetired(declared, catalog)).IsFalse();
    await Assert.That(options.IsRetired("Contracts.Deleted, Contracts", catalog)).IsTrue();
    await Assert.That(options.EffectiveNames(null)).IsEquivalentTo([declared, "Contracts.Deleted"])
      .Because("a host without a catalog declares no type a name could collide with");
    await Assert.That(options.StillDeclared(null)).IsEmpty();
  }

  [Test]
  public async Task Validate_ABlankName_OrABatchLimitBelowOne_FailsAsync() {
    var options = new RetiredEventTypesOptions { Names = { "  " }, BatchStreams = 0, MaxBatchesPerCycle = -1 };

    var failures = RetiredEventTypesOptions.Validate(options);

    await Assert.That(failures.Count).IsEqualTo(3);
    await Assert.That(failures.Any(f => f.Contains("Names", StringComparison.Ordinal))).IsTrue();
    await Assert.That(failures.Any(f => f.Contains("BatchStreams", StringComparison.Ordinal))).IsTrue();
    await Assert.That(failures.Any(f => f.Contains("MaxBatchesPerCycle", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Validator_StopsTheHostOnlyOnAValueItCannotApplyAsync() {
    var validator = new RetiredEventTypesOptionsValidator();

    await Assert.That(validator.Validate(null, new RetiredEventTypesOptions { BatchStreams = 0 }).Failed).IsTrue();
    await Assert.That(validator.Validate(null, new RetiredEventTypesOptions {
      Names = { TypeNameFormatter.FormatClrTypeName(typeof(StillHere)) },
    }).Succeeded).IsTrue();
    await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
  }

  private static MessageTypeCatalogEntry _event(Type type) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null);
}
