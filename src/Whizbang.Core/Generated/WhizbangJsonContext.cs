using System;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Core.Serialization;

namespace Whizbang.Core.Generated;

/// <summary>
/// Auto-registration coordinator for Whizbang.Core's JsonSerializerContext instances.
/// Uses [ModuleInitializer] to register InfrastructureJsonContext, WhizbangIdJsonContext,
/// and MessageJsonContext with the global JsonContextRegistry.
/// </summary>
/// <remarks>
/// This class runs before Main() and registers Core's contexts FIRST, ensuring infrastructure
/// types (MessageHop, MessageId, CorrelationId) take precedence over application types.
/// No manual chaining required - JsonContextRegistry.CreateCombinedOptions() handles merging.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersContextsWithRegistry_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersConverters_Async</tests>
/// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RunsBeforeMain_ViaModuleInitializerAsync</tests>
public static class WhizbangJsonContext {
  /// <summary>
  /// Module initializer that registers Whizbang.Core's JsonSerializerContext instances.
  /// Runs automatically when the assembly is loaded - no explicit call needed.
  /// Registers in priority order: WhizbangId → Infrastructure → Message.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersContextsWithRegistry_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RegistersConverters_Async</tests>
  /// <tests>tests/Whizbang.Core.Tests/Generated/WhizbangJsonContextTests.cs:Initialize_RunsBeforeMain_ViaModuleInitializerAsync</tests>
  // CA2255: Intentional use of ModuleInitializer in library code for AOT-compatible JSON context registration
#pragma warning disable CA2255
  [ModuleInitializer]
#pragma warning restore CA2255
  public static void Initialize() {
    // Register Core contexts in priority order
    // WhizbangIdJsonContext FIRST to ensure custom converters for MessageId/CorrelationId take precedence
    // InfrastructureJsonContext SECOND for MessageHop, ServiceInstanceInfo (which use MessageId/CorrelationId)
    JsonContextRegistry.RegisterContext(WhizbangIdJsonContext.Default);
    JsonContextRegistry.RegisterContext(InfrastructureJsonContext.Default);
    JsonContextRegistry.RegisterContext(MessageJsonContext.Default);

    // Register WhizbangId converter instances from Whizbang.Core (no reflection - AOT compatible!)
    // This allows InfrastructureJsonContext to find them via TryGetTypeInfoForRuntimeCustomConverter
    JsonContextRegistry.RegisterConverter(new ValueObjects.MessageIdJsonConverter());
    JsonContextRegistry.RegisterConverter(new ValueObjects.CorrelationIdJsonConverter());
  }
}
