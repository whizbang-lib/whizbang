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
public class DbContextRegistrationRegistryTests {
  [Before(Test)]
  public void ResetStaticState() {
    // Reset _registrations list
    var registrationsField = typeof(DbContextRegistrationRegistry)
        .GetField("_registrations", BindingFlags.Static | BindingFlags.NonPublic)!;
    var list = (System.Collections.IList)registrationsField.GetValue(null)!;
    list.Clear();

    // Reset _invoked ConditionalWeakTable by replacing it
    var invokedField = typeof(DbContextRegistrationRegistry)
        .GetField("_invoked", BindingFlags.Static | BindingFlags.NonPublic)!;
    var table = invokedField.GetValue(null)!;
    // ConditionalWeakTable has Clear() in .NET 9+
    var clearMethod = table.GetType().GetMethod("Clear")!;
    clearMethod.Invoke(table, null);
  }

  [Test]
  public async Task InvokeRegistration_CallsMatchingCallbackAsync() {
    // Arrange
    var invoked = false;
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { invoked = true; });
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
        (_, _) => { firstCalled = true; });
    DbContextRegistrationRegistry.Register<FakeRegDbContextA>(
        (_, _) => { secondCalled = true; });
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
        (_, _) => { callCount++; });
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
