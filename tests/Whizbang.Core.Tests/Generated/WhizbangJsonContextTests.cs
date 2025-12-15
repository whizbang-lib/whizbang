using TUnit.Assertions;
using Whizbang.Core.Generated;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Tests for WhizbangJsonContext - AOT-compatible JSON context registration.
/// </summary>
[Category("Serialization")]
public class WhizbangJsonContextTests {

  [Test]
  public async Task Initialize_RegistersContextsWithRegistry_Async() {
    // NOTE: This test needs implementation - track test gaps with grep 'NotImplementedException'
    // Should verify that WhizbangJsonContext.Initialize() registers:
    // - WhizbangIdJsonContext.Default
    // - InfrastructureJsonContext.Default
    // - MessageJsonContext.Default
    // Use JsonContextRegistry.RegisteredCount to verify registration count
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task Initialize_RegistersConverters_Async() {
    // NOTE: This test needs implementation - track test gaps with grep 'NotImplementedException'
    // Should verify that WhizbangJsonContext.Initialize() registers:
    // - MessageIdJsonConverter instance
    // - CorrelationIdJsonConverter instance
    // Use JsonContextRegistry.CreateCombinedOptions() to verify converters are in collection
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task Initialize_RunsBeforeMain_ViaModuleInitializerAsync() {
    // NOTE: This test needs implementation - track test gaps with grep 'NotImplementedException'
    // Should verify that Initialize() has already run before test execution
    // Verify JsonContextRegistry has contexts registered
    // This validates [ModuleInitializer] attribute worked correctly
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }
}
