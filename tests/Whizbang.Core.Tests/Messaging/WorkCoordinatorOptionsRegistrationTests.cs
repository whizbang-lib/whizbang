using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for WorkCoordinatorOptions service registration patterns.
/// Validates that the generated registration code (from EFCoreSnippets.cs) correctly
/// supports both direct registration and IOptions&lt;T&gt; pattern.
/// </summary>
/// <docs>components/workers/work-coordinator</docs>
public class WorkCoordinatorOptionsRegistrationTests {

  /// <summary>
  /// Verifies that when WorkCoordinatorOptions is configured via Configure&lt;T&gt;(),
  /// the resolved options reflect the configured values.
  /// This tests the IOptions&lt;T&gt; pattern support added in Phase 1 fix.
  /// </summary>
  [Test]
  public async Task WorkCoordinatorOptions_WhenConfiguredViaIOptions_ShouldResolveCorrectlyAsync() {
    // Arrange - Configure via IOptions<T> pattern (how users typically configure)
    var services = new ServiceCollection();
    services.Configure<WorkCoordinatorOptions>(opts => {
      opts.DebugMode = true;
      opts.IntervalMilliseconds = 500;
      opts.PartitionCount = 5000;
      opts.LeaseSeconds = 120;
      opts.StaleThresholdSeconds = 180;
    });

    // Simulate the generated registration pattern from EFCoreSnippets.cs
    // This is the actual pattern used in generated code
    if (!services.Any(sd => sd.ServiceType == typeof(WorkCoordinatorOptions))) {
      services.AddSingleton<WorkCoordinatorOptions>(sp => {
        // Check if user configured via IOptions<T> pattern
        var optionsAccessor = sp.GetService<IOptions<WorkCoordinatorOptions>>();
        if (optionsAccessor is not null) {
          return optionsAccessor.Value;
        }
        // Fallback to default
        return new WorkCoordinatorOptions();
      });
    }

    var provider = services.BuildServiceProvider();

    // Act
    var resolvedOptions = provider.GetRequiredService<WorkCoordinatorOptions>();

    // Assert - Verify configured values are resolved
    await Assert.That(resolvedOptions.DebugMode).IsTrue();
    await Assert.That(resolvedOptions.IntervalMilliseconds).IsEqualTo(500);
    await Assert.That(resolvedOptions.PartitionCount).IsEqualTo(5000);
    await Assert.That(resolvedOptions.LeaseSeconds).IsEqualTo(120);
    await Assert.That(resolvedOptions.StaleThresholdSeconds).IsEqualTo(180);
  }

  /// <summary>
  /// Verifies that when WorkCoordinatorOptions is not configured via IOptions&lt;T&gt;,
  /// default values are used.
  /// </summary>
  [Test]
  public async Task WorkCoordinatorOptions_WhenNotConfigured_ShouldUseDefaultAsync() {
    // Arrange - No IOptions<T> configuration
    var services = new ServiceCollection();

    // Simulate the generated registration pattern from EFCoreSnippets.cs
    if (!services.Any(sd => sd.ServiceType == typeof(WorkCoordinatorOptions))) {
      services.AddSingleton<WorkCoordinatorOptions>(sp => {
        // Check if user configured via IOptions<T> pattern
        var optionsAccessor = sp.GetService<IOptions<WorkCoordinatorOptions>>();
        if (optionsAccessor is not null) {
          return optionsAccessor.Value;
        }
        // Fallback to default
        return new WorkCoordinatorOptions();
      });
    }

    var provider = services.BuildServiceProvider();

    // Act
    var resolvedOptions = provider.GetRequiredService<WorkCoordinatorOptions>();

    // Assert - Verify default values
    await Assert.That(resolvedOptions.DebugMode).IsFalse();
    await Assert.That(resolvedOptions.IntervalMilliseconds).IsEqualTo(100); // Default
    await Assert.That(resolvedOptions.PartitionCount).IsEqualTo(10000); // Default
    await Assert.That(resolvedOptions.LeaseSeconds).IsEqualTo(300); // Default
    await Assert.That(resolvedOptions.StaleThresholdSeconds).IsEqualTo(600); // Default
  }

  /// <summary>
  /// Verifies that direct registration takes precedence over IOptions&lt;T&gt; pattern.
  /// When WorkCoordinatorOptions is already registered directly, the IOptions&lt;T&gt;
  /// configuration should be ignored.
  /// </summary>
  [Test]
  public async Task WorkCoordinatorOptions_WhenDirectlyRegistered_ShouldIgnoreIOptionsAsync() {
    // Arrange - Direct registration first
    var services = new ServiceCollection();
    services.AddSingleton(new WorkCoordinatorOptions {
      DebugMode = true,
      IntervalMilliseconds = 999
    });

    // Then configure via IOptions<T> (should be ignored)
    services.Configure<WorkCoordinatorOptions>(opts => {
      opts.DebugMode = false;
      opts.IntervalMilliseconds = 111;
    });

    // Simulate the generated registration pattern - should NOT add since already registered
    if (!services.Any(sd => sd.ServiceType == typeof(WorkCoordinatorOptions))) {
      services.AddSingleton<WorkCoordinatorOptions>(sp => {
        var optionsAccessor = sp.GetService<IOptions<WorkCoordinatorOptions>>();
        if (optionsAccessor is not null) {
          return optionsAccessor.Value;
        }
        return new WorkCoordinatorOptions();
      });
    }

    var provider = services.BuildServiceProvider();

    // Act
    var resolvedOptions = provider.GetRequiredService<WorkCoordinatorOptions>();

    // Assert - Direct registration values should be used
    await Assert.That(resolvedOptions.DebugMode).IsTrue();
    await Assert.That(resolvedOptions.IntervalMilliseconds).IsEqualTo(999);
  }

  /// <summary>
  /// Verifies that partial IOptions configuration works correctly.
  /// Only configured properties should be changed; others should use defaults.
  /// </summary>
  [Test]
  public async Task WorkCoordinatorOptions_WhenPartiallyConfiguredViaIOptions_ShouldMergeWithDefaultsAsync() {
    // Arrange - Only configure DebugMode
    var services = new ServiceCollection();
    services.Configure<WorkCoordinatorOptions>(opts => {
      opts.DebugMode = true;
      // Leave other properties at their defaults
    });

    // Simulate the generated registration pattern
    if (!services.Any(sd => sd.ServiceType == typeof(WorkCoordinatorOptions))) {
      services.AddSingleton<WorkCoordinatorOptions>(sp => {
        var optionsAccessor = sp.GetService<IOptions<WorkCoordinatorOptions>>();
        if (optionsAccessor is not null) {
          return optionsAccessor.Value;
        }
        return new WorkCoordinatorOptions();
      });
    }

    var provider = services.BuildServiceProvider();

    // Act
    var resolvedOptions = provider.GetRequiredService<WorkCoordinatorOptions>();

    // Assert - DebugMode changed, others at defaults
    await Assert.That(resolvedOptions.DebugMode).IsTrue();
    await Assert.That(resolvedOptions.IntervalMilliseconds).IsEqualTo(100); // Default
    await Assert.That(resolvedOptions.PartitionCount).IsEqualTo(10000); // Default
  }
}
