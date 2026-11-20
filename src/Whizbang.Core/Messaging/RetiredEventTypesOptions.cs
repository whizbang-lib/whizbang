// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The event types the application deleted and retired, bound from <c>Whizbang:EventStore:RetiredTypes</c> (#2329). A
/// retired type is an eligibility rule: no integrity digest counts it on either side of a service pair, the receive
/// boundary discards it, and the maintenance cycle purges its stored events from every stream, a bounded batch at a
/// time, whether or not the type's class still exists anywhere.
/// </summary>
/// <remarks>
/// <para>
/// The rule is part of the type's rule hash, so two services that disagree on whether a type is retired halt their
/// pair through the pair guard until both declare it: declare a retirement in every service that stores the type.
/// </para>
/// <para>
/// A name is matched without its assembly. A name this service still declares as an event type stops the host at
/// startup (<see cref="RetiredEventTypesOptionsValidator"/>): a retirement purges events, and a type that is still
/// declared still has readers here. A name nothing is stored under is reported by the purge, once per process.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/retired-event-types</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs</tests>
public sealed class RetiredEventTypesOptions {
  /// <summary>The configuration section: <c>Whizbang:EventStore:RetiredTypes</c>.</summary>
  public const string SECTION = "Whizbang:EventStore:RetiredTypes";

  /// <summary>The default <see cref="BatchStreams"/>.</summary>
  public const int DEFAULT_BATCH_STREAMS = 500;

  /// <summary>The default <see cref="MaxBatchesPerCycle"/>.</summary>
  public const int DEFAULT_MAX_BATCHES_PER_CYCLE = 10;

  /// <summary>
  /// The retired event types, by name, with or without the assembly (<c>Contracts.CouponIssued</c>). Empty by default:
  /// nothing is retired.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Configuration/ProcessWideOptionsBindingTests.cs:RetiredEventTypes_BindFromTheirSection_AndValidateAtStartupAsync</tests>
  public List<string> Names { get; } = [];

  /// <summary>
  /// The most streams one purge batch takes, each with every stored event of the retired types in it. One batch is one
  /// transaction. Default 500.
  /// </summary>
  public int BatchStreams { get; set; } = DEFAULT_BATCH_STREAMS;

  /// <summary>The most purge batches one maintenance cycle runs. Default 10.</summary>
  public int MaxBatchesPerCycle { get; set; } = DEFAULT_MAX_BATCHES_PER_CYCLE;

  /// <summary>The retired names without their assembly, each once, in ordinal order; blank names are left out.</summary>
  /// <returns>The names.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:RetiredNames_AreTheNamesWithoutTheirAssembly_OnceEach_InOrdinalOrderAsync</tests>
  public IReadOnlyList<string> RetiredNames() =>
    [.. Names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(RuleTypeName.Of).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

  /// <summary>Whether <paramref name="eventType"/>, in any form, is retired.</summary>
  /// <param name="eventType">A stored, wire or bare type name.</param>
  /// <returns>True when its name without the assembly is retired.</returns>
  public bool IsRetired(string eventType) {
    ArgumentNullException.ThrowIfNull(eventType);
    var name = RuleTypeName.Of(eventType);
    return Names.Exists(n => !string.IsNullOrWhiteSpace(n) && string.Equals(RuleTypeName.Of(n), name, StringComparison.Ordinal));
  }

  /// <summary>
  /// Why <paramref name="options"/> cannot be applied: a blank name, a batch limit below one, or a name
  /// <paramref name="catalog"/> still declares as an event type.
  /// </summary>
  /// <param name="options">The options.</param>
  /// <param name="catalog">This service's message types, or null when it has none.</param>
  /// <returns>The failures; empty when the options are valid.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:Validate_ANameThisServiceStillDeclares_FailsAndNamesItAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:Validate_ABlankName_OrABatchLimitBelowOne_FailsAsync</tests>
  public static IReadOnlyList<string> Validate(RetiredEventTypesOptions options, IMessageTypeCatalog? catalog) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = new List<string>();
    if (options.Names.Exists(string.IsNullOrWhiteSpace)) {
      failures.Add($"{SECTION}:Names holds a blank name; name each retired event type.");
    }
    if (options.BatchStreams < 1) {
      failures.Add($"{SECTION}:BatchStreams must be at least 1, was {options.BatchStreams}.");
    }
    if (options.MaxBatchesPerCycle < 1) {
      failures.Add($"{SECTION}:MaxBatchesPerCycle must be at least 1, was {options.MaxBatchesPerCycle}.");
    }
    var declared = new HashSet<string>(
      (catalog?.GetAll() ?? []).Where(e => e.Kind == "event").Select(e => RuleTypeName.Of(e.ClrTypeName)), StringComparer.Ordinal);
    foreach (var name in options.RetiredNames().Where(declared.Contains)) {
      failures.Add($"{SECTION}:Names retires '{name}', which this service still declares as an event type. A retirement "
        + "purges the type's stored events, so it is declared only once the type is deleted; remove the name or delete the type.");
    }
    return failures;
  }
}

/// <summary>
/// Stops the host at startup on <see cref="RetiredEventTypesOptions"/> that cannot be applied (#2329), reading this
/// service's message types from the registered <see cref="IMessageTypeCatalog"/>.
/// </summary>
/// <param name="services">The host's services.</param>
/// <docs>fundamentals/events/retired-event-types#declaring-a-retired-type</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/RetiredEventTypesOptionsTests.cs:Validator_ReadsTheRegisteredCatalog_AndFailsTheHostOnAStillDeclaredNameAsync</tests>
public sealed class RetiredEventTypesOptionsValidator(IServiceProvider services) : IValidateOptions<RetiredEventTypesOptions> {
  /// <inheritdoc />
  public ValidateOptionsResult Validate(string? name, RetiredEventTypesOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var failures = RetiredEventTypesOptions.Validate(options, services.GetService<IMessageTypeCatalog>());
    return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
  }
}
