// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for DbContextRegistrationRegistry static registry.
/// Each test resets static state via reflection to ensure isolation.
/// </summary>
[NotInParallel("DbContextRegistrationRegistry")]
[Category("Shard2")]
public class DbContextRegistrationRegistryTests {
  private IDisposable? _restore;

  [Before(Test)]
  public void ResetStaticState() => _restore = DbContextRegistrationRegistry.ResetForTesting();

  // Puts back the generated registrations this class cleared: later tests in the same process build turnkey hosts
  // that rely on them, and module initializers never run again.
  [After(Test)]
  public void RestoreStaticState() => _restore?.Dispose();

  [Test]
  public async Task ResetForTesting_DisposingTheScope_PutsBackWhatWasRegisteredAsync() {
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>((_, _) => { });

    using (DbContextRegistrationRegistry.ResetForTesting()) {
      await Assert.That(DbContextRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(FakeRegDbContextA))).IsFalse()
        .Because("inside the scope the registry is empty");
    }

    await Assert.That(DbContextRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(FakeRegDbContextA))).IsTrue()
      .Because("a reset that is not undone unregisters every generated context for the rest of the process, and a "
        + "later test that builds a turnkey host then fails with no service registered for its context");
  }

  [Test]
  public async Task InvokeRegistration_CallsMatchingCallbackAsync() {
    // Arrange
    var invoked = false;
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => invoked = true);
    var services = new ServiceCollection();

    // Act
    var result = DbContextRegistrationRegistry.InvokeRegistration(
        services, typeof(FakeRegDbContextA));

    // Assert
    await Assert.That(result).IsTrue();
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task InvokeRegistration_AlreadyInvoked_ReturnsFalseAsync() {
    // Arrange
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { });
    var services = new ServiceCollection();

    // Act — invoke once, then again
    DbContextRegistrationRegistry.InvokeRegistration(
        services, typeof(FakeRegDbContextA));
    var result = DbContextRegistrationRegistry.InvokeRegistration(
        services, typeof(FakeRegDbContextA));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task InvokeRegistration_DbContextAlreadyRegistered_SkipsAndReturnsFalseAsync() {
    // Arrange — register a callback, but also pre-register the DbContext type in services
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { });
    var services = new ServiceCollection();
    services.AddSingleton<FakeRegDbContextA>();

    // Act
    var result = DbContextRegistrationRegistry.InvokeRegistration(
        services, typeof(FakeRegDbContextA));

    // Assert — should skip because DbContext is already in the service collection
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task InvokeRegistration_NoMatchingRegistration_ReturnsFalseAsync() {
    // Arrange — register for type A, invoke for type B
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { });
    var services = new ServiceCollection();

    // Act
    var result = DbContextRegistrationRegistry.InvokeRegistration(
        services, typeof(FakeRegDbContextB));

    // Assert
    await Assert.That(result).IsFalse();
  }

  [Test]
  public async Task InvokeRegistration_MultipleRegistrations_LatestWinsAsync() {
    // Arrange — register same type twice, latest should win
    var firstCalled = false;
    var secondCalled = false;
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => firstCalled = true);
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => secondCalled = true);
    var services = new ServiceCollection();

    // Act
    DbContextRegistrationRegistry.InvokeRegistration(
        services, typeof(FakeRegDbContextA));

    // Assert — latest registration wins (reverse iteration)
    await Assert.That(firstCalled).IsFalse();
    await Assert.That(secondCalled).IsTrue();
  }

  [Test]
  public async Task InvokeRegistration_DifferentServiceCollections_InvokesBothAsync() {
    // Arrange
    var callCount = 0;
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => callCount++);
    var services1 = new ServiceCollection();
    var services2 = new ServiceCollection();

    // Act — invoke on two different service collections
    var result1 = DbContextRegistrationRegistry.InvokeRegistration(
        services1, typeof(FakeRegDbContextA));
    var result2 = DbContextRegistrationRegistry.InvokeRegistration(
        services2, typeof(FakeRegDbContextA));

    // Assert — ConditionalWeakTable tracks per-collection, so both invoke
    await Assert.That(result1).IsTrue();
    await Assert.That(result2).IsTrue();
    await Assert.That(callCount).IsEqualTo(2);
  }

  [Test]
  public async Task HasRegistration_ReturnsTrueForRegisteredTypeAsync() {
    // Arrange
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { });

    // Act & Assert
    await Assert.That(
        DbContextRegistrationRegistry.HasRegistration(typeof(FakeRegDbContextA)))
        .IsTrue();
  }

  [Test]
  public async Task HasRegistration_ReturnsFalseForUnregisteredTypeAsync() {
    // Act & Assert
    await Assert.That(
        DbContextRegistrationRegistry.HasRegistration(typeof(FakeRegDbContextB)))
        .IsFalse();
  }

  [Test]
  public async Task GetRegisteredDbContextTypes_ReturnsDistinctTypesAsync() {
    // Arrange — register same type twice and a different type once
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { });
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { });
    DbContextRegistrationRegistry.Register<FakeRegDbContextB>(
        (_, _) => { });

    // Act
    var types = DbContextRegistrationRegistry.GetRegisteredDbContextTypes();

    // Assert — should be deduplicated
    await Assert.That(types).Count().IsEqualTo(2);
    await Assert.That(types).Contains(typeof(FakeRegDbContextA));
    await Assert.That(types).Contains(typeof(FakeRegDbContextB));
  }

  // --- Fake DbContext types for test isolation ---
  private sealed class FakeRegDbContextA;
  private sealed class FakeRegDbContextB;
}
