// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Static registry for model registration callbacks.
/// Consumer assemblies register their discovered models via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every assembly's models are registered (#2201).</strong> Each assembly keeps one entry, keyed by its
/// name: a re-registration from the same assembly (a test calling the generated <c>Initialize()</c> again, a hot
/// reload) replaces that assembly's own entry in place, and two different assemblies never displace each other. The
/// driver runs every entry's registrar for its DbContext, in the order the assemblies registered, so the latest
/// assembly's registration of a model is the one the container serves. This used to run only the latest entry, so
/// models declared in every other assembly were never registered.
/// </para>
/// <para>
/// A model two different assemblies both register for the same DbContext is a collision: the latest registration is
/// used, and startup reports it naming the model and both assemblies, or stops under
/// <c>Whizbang:Startup:RegistrationCollisions = Fail</c> (<see cref="RegistrationCollisions"/>).
/// </para>
/// <para>
/// Tracks which (ServiceCollection, DbContext, entry) tuples have been invoked to prevent duplicate registrations
/// within the same ServiceCollection. Uses ConditionalWeakTable to track per-ServiceCollection without preventing
/// garbage collection.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs</tests>
public static class ModelRegistrationRegistry {
  private const string MODEL_REGISTRATION = "Perspective model";
  private const long COLLISIONS_REPORTED = -1;

  private sealed record Entry(
      long Id,
      string? AssemblyName,
      IReadOnlyList<(Type DbContextType, Type ModelType)> Models,
      Action<IServiceCollection, Type, IDbUpsertStrategy> Registrar);

  private static readonly List<Entry> _entries = [];
  private static readonly ConditionalWeakTable<IServiceCollection, HashSet<(Type dbContextType, long entryId)>> _invoked = [];
  private static readonly Lock _lock = new();
  private static long _nextId;

  /// <summary>
  /// Registers a callback that will register discovered perspective models, as an entry of no named assembly: it is
  /// never replaced and declares no models for the collisions report.
  /// </summary>
  /// <param name="registrar">Callback that registers models with the service collection.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:RegisterModels_WithValidRegistrar_StoresCallbackAsync</tests>
  public static void RegisterModels(Action<IServiceCollection, Type, IDbUpsertStrategy> registrar) =>
    _register(null, [], registrar);

  /// <summary>
  /// Registers <paramref name="assemblyName"/>'s model registration, replacing that assembly's earlier entry in
  /// place when it has one. The source-generated module initializer of every assembly with perspective models calls
  /// this.
  /// </summary>
  /// <param name="assemblyName">The registering assembly's name.</param>
  /// <param name="models">Each model the registrar registers, with its DbContext.</param>
  /// <param name="registrar">Callback that registers models with the service collection.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:RegisterModels_TheSameAssemblyAgain_ReplacesOnlyItsOwnEntryAsync</tests>
  public static void RegisterModels(
      string assemblyName,
      IReadOnlyList<(Type DbContextType, Type ModelType)> models,
      Action<IServiceCollection, Type, IDbUpsertStrategy> registrar) {
    ArgumentNullException.ThrowIfNull(assemblyName);
    ArgumentNullException.ThrowIfNull(models);
    _register(assemblyName, models, registrar);
  }

  private static void _register(
      string? assemblyName,
      IReadOnlyList<(Type DbContextType, Type ModelType)> models,
      Action<IServiceCollection, Type, IDbUpsertStrategy> registrar) {
    lock (_lock) {
      var entry = new Entry(++_nextId, assemblyName, models, registrar);
      var existing = assemblyName is null ? -1 : _entries.FindIndex(e => e.AssemblyName == assemblyName);
      if (existing >= 0) {
        _entries[existing] = entry;
      } else {
        _entries.Add(entry);
      }
    }
  }

  /// <summary>
  /// Clears registrars and the invoked set; for tests that share this process-wide registry. The
  /// returned scope puts the registrars back: they come from module initializers, which never run
  /// again, so a reset that is not undone silently unregisters every generated model for the rest of
  /// the process.
  /// </summary>
  internal static IDisposable ResetForTesting() {
    lock (_lock) {
      var saved = _entries.ToArray();
      _entries.Clear();
      _invoked.Clear();
      return new RegistrarRestore(saved);
    }
  }

  private sealed class RegistrarRestore(Entry[] saved) : IDisposable {
    public void Dispose() {
      lock (_lock) {
        _entries.Clear();
        _entries.AddRange(saved);
        _invoked.Clear();
      }
    }
  }

  /// <summary>
  /// Invokes every registered model registration callback for the given ServiceCollection and DbContext, in the
  /// order the assemblies registered, and adds the startup report of any model two assemblies both register for it.
  /// Called by driver extensions (InMemory, Postgres) to register discovered models and infrastructure.
  /// If no registrars have been set (module initializers haven't run or no DbContext found), does nothing gracefully.
  /// Each (ServiceCollection, DbContext, entry) runs once, so registering twice adds nothing; a replaced entry is a
  /// new entry and runs.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <param name="dbContextType">The DbContext type.</param>
  /// <param name="upsertStrategy">The database-specific upsert strategy.</param>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:InvokeRegistration_EveryAssemblysRegistrar_RunsAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ModelRegistrationRegistryTests.cs:InvokeRegistration_AModelTwoAssembliesRegister_IsReportedAsync</tests>
  internal static void InvokeRegistration(
      IServiceCollection services,
      Type dbContextType,
      IDbUpsertStrategy upsertStrategy) {

    // If no registrars were set, the module initializers haven't run or no DbContext was discovered.
    // Either way, gracefully skip registration rather than throwing an error.
    lock (_lock) {
      if (_entries.Count == 0) {
        return;
      }

      // Get or create the invocation tracking set for this ServiceCollection
      // ConditionalWeakTable ensures we don't prevent ServiceCollection from being garbage collected
      if (!_invoked.TryGetValue(services, out var invokedSet)) {
        invokedSet = [];
        _invoked.Add(services, invokedSet);
      }

      foreach (var entry in _entries.Where(entry => invokedSet.Add((dbContextType, entry.Id)))) {
        entry.Registrar(services, dbContextType, upsertStrategy);
      }

      var collisions = RegistrationCollisions.Find(MODEL_REGISTRATION, _entries
        .Where(entry => entry.AssemblyName is not null)
        .Select(entry => (entry.AssemblyName!, entry.Models
          .Where(model => model.DbContextType == dbContextType)
          .Select(model => $"{TypeNameFormatter.DisplayName(model.ModelType)} in {TypeNameFormatter.DisplayName(dbContextType)}"))));
      if (collisions.Count > 0 && invokedSet.Add((dbContextType, COLLISIONS_REPORTED))) {
        services.AddRegistrationCollisionSource(new FixedCollisions(collisions));
      }
    }
  }

  private sealed class FixedCollisions(IReadOnlyList<RegistrationCollision> collisions) : IRegistrationCollisionSource {
    public IReadOnlyList<RegistrationCollision> GetCollisions() => collisions;
  }
}
