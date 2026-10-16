// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;

/// <summary>
/// TUnit ClassDataSource for ServiceBus emulator fixture.
/// Provides a single shared emulator instance for all tests in the assembly.
/// </summary>
public sealed class ServiceBusEmulatorFixtureSource {
  private static ServiceBusEmulatorFixture? _fixture;
  private static readonly SemaphoreSlim _initLock = new(1, 1);
  private static bool _initialized;
  private static readonly TimeSpan _setupDeadline = TimeSpan.FromMinutes(10);

  /// <summary>
  /// Default constructor required by TUnit ClassDataSource.
  /// Synchronously initializes the emulator before tests run.
  /// </summary>
  public ServiceBusEmulatorFixtureSource() {
    // Synchronously wait for initialization to complete
    // This ensures the emulator is ready before test classes are constructed
    _initializeAsync().GetAwaiter().GetResult();
    Emulator = Fixture;
  }

  /// <summary>The emulator this instance initialized: the same shared fixture <see cref="Fixture"/> exposes.</summary>
  public ServiceBusEmulatorFixture Emulator { get; }

  /// <summary>
  /// Gets the initialized ServiceBus emulator fixture.
  /// </summary>
  // Instance property because TUnit ClassDataSource requires instance access
#pragma warning disable CA1822 // Member does not access instance data
  public static ServiceBusEmulatorFixture Fixture =>
    _fixture ?? throw new InvalidOperationException("Emulator fixture not initialized");
#pragma warning restore CA1822

  /// <summary>
  /// Initializes the emulator once per assembly.
  /// </summary>
  private static async Task _initializeAsync() {
    if (_initialized) {
      return;
    }

    await _initLock.WaitAsync();
    try {
      if (_initialized) {
        return;
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[EMULATOR FIXTURE SOURCE] Initializing Azure Service Bus Emulator...");
      Console.WriteLine("================================================================================");

      _fixture = new ServiceBusEmulatorFixture();
      // A setup that stalls must fail with the step it stalled on, not hang until the CI job is killed (#1366).
      using var deadline = new CancellationTokenSource(_setupDeadline);
      try {
        await _fixture.InitializeAsync(deadline.Token);
      } catch (OperationCanceledException) when (deadline.IsCancellationRequested) {
        throw new TimeoutException(
          $"Service Bus emulator setup did not finish within {_setupDeadline.TotalMinutes} minutes; it stopped while {_fixture.CurrentStep}.");
      }

      Console.WriteLine("================================================================================");
      Console.WriteLine("[EMULATOR FIXTURE SOURCE] ✅ Emulator ready!");
      Console.WriteLine("================================================================================");

      _initialized = true;
    } finally {
      _initLock.Release();
    }
  }
}
