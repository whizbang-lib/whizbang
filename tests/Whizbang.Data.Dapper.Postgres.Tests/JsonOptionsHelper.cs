using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Creates JsonSerializerOptions that include both the local assembly's generated contexts
/// and the Whizbang.Testing contract test JSON context (for contract test types like TestEvent).
/// </summary>
internal static class JsonOptionsHelper {
  public static JsonSerializerOptions CreateOptions() {
    var resolvers = new IJsonTypeInfoResolver[] {
      global::Whizbang.Core.Generated.WhizbangIdJsonContext.Default,
      global::Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangIdJsonContext.Default,
      global::Whizbang.Data.Dapper.Postgres.Tests.Generated.MessageJsonContext.Default,
      ContractTestJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default
    };

    return new JsonSerializerOptions {
      TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers),
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
  }
}
