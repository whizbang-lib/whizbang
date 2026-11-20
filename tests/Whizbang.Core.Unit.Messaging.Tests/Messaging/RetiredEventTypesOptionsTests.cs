// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2329: a deleted event type is retired by naming it under <c>Whizbang:EventStore:RetiredTypes</c>. The names are
/// read without their assembly, and a name this service still declares as an event type, a blank name or a batch limit
/// below one stops the host at startup.
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
      Names = { "Contracts.Zeta, Contracts", "global::Contracts.Alpha", "Contracts.Zeta", " Contracts.Outer+Nested " },
    };

    await Assert.That(options.RetiredNames()).IsEquivalentTo(["Contracts.Alpha", "Contracts.Outer+Nested", "Contracts.Zeta"]);
    await Assert.That(options.IsRetired("Contracts.Alpha, Other.Assembly")).IsTrue()
      .Because("a stored or wire name carries an assembly; the type is matched by its name alone");
    await Assert.That(options.IsRetired("Contracts.Beta")).IsFalse();
  }

  [Test]
  public async Task Validate_TheDefaults_AndRetiredNamesTheCatalogNoLongerDeclares_PassAsync() {
    var options = new RetiredEventTypesOptions { Names = { "Contracts.Deleted" } };

    await Assert.That(RetiredEventTypesOptions.Validate(new RetiredEventTypesOptions(), null)).IsEmpty();
    await Assert.That(RetiredEventTypesOptions.Validate(options, new Catalog(_event(typeof(StillHere))))).IsEmpty();
  }

  [Test]
  public async Task Validate_ANameThisServiceStillDeclares_FailsAndNamesItAsync() {
    var declared = TypeNameFormatter.FormatClrTypeName(typeof(StillHere));
    var options = new RetiredEventTypesOptions { Names = { TypeNameFormatter.Format(typeof(StillHere)) } };

    var failures = RetiredEventTypesOptions.Validate(options, new Catalog(_event(typeof(StillHere))));

    await Assert.That(failures.Count).IsEqualTo(1);
    await Assert.That(failures[0]).Contains(declared)
      .Because("retiring a type the service still declares would purge events it still reads");
    await Assert.That(failures[0]).Contains(RetiredEventTypesOptions.SECTION);
  }

  [Test]
  public async Task Validate_ABlankName_OrABatchLimitBelowOne_FailsAsync() {
    var options = new RetiredEventTypesOptions { Names = { "  " }, BatchStreams = 0, MaxBatchesPerCycle = -1 };

    var failures = RetiredEventTypesOptions.Validate(options, null);

    await Assert.That(failures.Count).IsEqualTo(3);
    await Assert.That(failures.Any(f => f.Contains("Names", StringComparison.Ordinal))).IsTrue();
    await Assert.That(failures.Any(f => f.Contains("BatchStreams", StringComparison.Ordinal))).IsTrue();
    await Assert.That(failures.Any(f => f.Contains("MaxBatchesPerCycle", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Validator_ReadsTheRegisteredCatalog_AndFailsTheHostOnAStillDeclaredNameAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IMessageTypeCatalog>(new Catalog(_event(typeof(StillHere))));
    await using var provider = services.BuildServiceProvider();
    var validator = new RetiredEventTypesOptionsValidator(provider);

    var bad = validator.Validate(null, new RetiredEventTypesOptions { Names = { TypeNameFormatter.FormatClrTypeName(typeof(StillHere)) } });
    var good = validator.Validate(null, new RetiredEventTypesOptions { Names = { "Contracts.Deleted" } });

    await Assert.That(bad.Failed).IsTrue();
    await Assert.That(good.Succeeded).IsTrue();
    await Assert.That(new RetiredEventTypesOptionsValidator(new ServiceCollection().BuildServiceProvider())
      .Validate(null, new RetiredEventTypesOptions { Names = { "Contracts.Deleted" } }).Succeeded).IsTrue()
      .Because("a host without a catalog has no declared type a name could collide with");
    await Assert.That(() => validator.Validate(null, null!)).Throws<ArgumentNullException>();
  }

  private static MessageTypeCatalogEntry _event(Type type) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), "event", null);
}
