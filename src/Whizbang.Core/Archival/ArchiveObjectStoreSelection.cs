// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Archival;

/// <summary>
/// Which <see cref="IArchiveObjectStore"/> the archive writes through, and why (#2503): decided when the container is
/// built, from configuration and code, so a provider can activate from its settings without a registration call. A
/// selection with no store keeps the driver's built-in table store.
/// </summary>
/// <param name="Store">The object store, or <see langword="null"/> for none.</param>
/// <param name="Reason">Why, as the startup report writes it: the setting that named the account, or that code registered it.</param>
/// <docs>fundamentals/events/archive-store#registering-an-object-store</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs</tests>
public sealed record ArchiveObjectStoreSelection(IArchiveObjectStore? Store, string Reason) {
  /// <summary>The selected object store of <paramref name="services"/>, or <see langword="null"/> when none is selected.</summary>
  /// <param name="services">The service provider.</param>
  /// <returns>The object store, or <see langword="null"/>.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:Registration_ExplicitReplaces_TryAddKeepsTheFirst_AndTheStepIsAddedOnceAsync</tests>
  public static IArchiveObjectStore? Resolve(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return services.GetService<ArchiveObjectStoreSelection>()?.Store;
  }
}

/// <summary>
/// Registers the archive's object store: the selection the database driver's coordinator writes through, and the
/// maintenance step that deletes the objects no record names any more.
/// </summary>
/// <docs>fundamentals/events/archive-store#registering-an-object-store</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs</tests>
public static class ArchiveObjectStoreServiceCollectionExtensions {
  /// <summary>
  /// Selects the archive's object store, replacing any selection made before; a selection made later by
  /// <see cref="TryAddWhizbangArchiveObjectStore"/> (a provider activated from its settings) does not replace it.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="select">Builds the selection when the container is built.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:Registration_ExplicitReplaces_TryAddKeepsTheFirst_AndTheStepIsAddedOnceAsync</tests>
  public static IServiceCollection AddWhizbangArchiveObjectStore(
      this IServiceCollection services, Func<IServiceProvider, ArchiveObjectStoreSelection> select) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(select);
    services.RemoveAll<ArchiveObjectStoreSelection>();
    return services.TryAddWhizbangArchiveObjectStore(select);
  }

  /// <summary>Selects the archive's object store only when nothing has selected one.</summary>
  /// <param name="services">The service collection.</param>
  /// <param name="select">Builds the selection when the container is built.</param>
  /// <returns>The service collection.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveObjectStoreContractTests.cs:Registration_ExplicitReplaces_TryAddKeepsTheFirst_AndTheStepIsAddedOnceAsync</tests>
  public static IServiceCollection TryAddWhizbangArchiveObjectStore(
      this IServiceCollection services, Func<IServiceProvider, ArchiveObjectStoreSelection> select) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(select);
    services.TryAddSingleton(select);
    // Deletes the objects a purge (or a restore or delete whose own deletion failed) left queued (269); nothing without
    // a coordinator.
    services.TryAddEnumerable(ServiceDescriptor.Scoped<IMaintenanceStep, ArchiveObjectDeletionStep>(
      sp => new ArchiveObjectDeletionStep(sp.GetService<IArchiveStore>() as IArchiveObjectCoordinator)));
    return services;
  }
}
