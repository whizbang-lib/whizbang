// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// The collective event types a handler on this service applies, as a schema initialization reads them for its
/// associations step and its hash (#2145): from the consumer registry of the service provider passed to it, else of
/// the host the context was built from.
/// </summary>
/// <remarks>
/// A direct <c>EnsureWhizbangDatabaseInitializedAsync</c> call that passes no service provider reads the same answer
/// the host's own initialization read, so it finds the associations current instead of re-registering them without
/// the gate. Only a context with no host and no provider cannot say, and gates nothing.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events#only-handled-types-are-queued</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveAssociationRegistrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupPreflightHostTests.cs:ADirectDbContextInitialization_WaitsForTheHostsOwn_AndAppliesNothingAsync</tests>
public static class SchemaCollectiveHandlers {
  /// <summary>The handled collective event types, or null when neither provider can say.</summary>
  /// <param name="dbContext">The context being initialized.</param>
  /// <param name="serviceProvider">The service provider passed to the initialization, if any.</param>
  /// <returns>The handled collective event types, or <see langword="null"/>.</returns>
  public static IReadOnlyList<string>? Resolve(DbContext dbContext, IServiceProvider? serviceProvider) {
    ArgumentNullException.ThrowIfNull(dbContext);
    // Every EF Core context carries CoreOptionsExtension, as SchemaInitializationBarrier reads it.
    return ConsumedMessageTypes.HandledCollectiveEventTypes(
      serviceProvider
      ?? dbContext.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.ApplicationServiceProvider);
  }

  /// <summary>
  /// The event types this host's perspectives fan out (#2619), from the fan-out registry of the service provider passed
  /// to the initialization, else of the host the context was built from: each one's event-store type name, which the
  /// associations step writes as the type's route to the collective sink. Empty when neither provider declares one.
  /// </summary>
  /// <param name="dbContext">The context being initialized.</param>
  /// <param name="serviceProvider">The service provider passed to the initialization, if any.</param>
  /// <returns>The fan-out event types, possibly repeated when two perspectives fan out one type.</returns>
  /// <docs>fundamentals/messaging/collective-events#fan-out-rows</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CollectiveAssociationRegistrationTests.cs:AFanOut_IsWrittenAsAPerspectiveAssociationToTheSink_WithTheSinksNoFloorMarkerAsync</tests>
  public static IReadOnlyList<string> ResolveFanOutEventTypes(DbContext dbContext, IServiceProvider? serviceProvider) {
    ArgumentNullException.ThrowIfNull(dbContext);
    var services = serviceProvider
      ?? dbContext.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.ApplicationServiceProvider;
    return services is null ? [] : [.. services.GetPerspectiveFanOutEntries().Select(e => TypeNameFormatter.Format(e.EventType))];
  }
}
