using System.Text.Json.Serialization;
using Whizbang.Core.Observability;

namespace Whizbang.Testing.Contracts;

/// <summary>
/// JSON serialization context for contract test types (TestEvent, TestResponse).
/// This allows test projects using Dapper stores to serialize/deserialize these types
/// by including this context in their JsonSerializerOptions resolver chain.
/// </summary>
[JsonSerializable(typeof(TestEvent))]
[JsonSerializable(typeof(TestResponse))]
[JsonSerializable(typeof(MessageEnvelope<TestEvent>))]
[JsonSerializable(typeof(MessageEnvelope<TestResponse>))]
public partial class ContractTestJsonContext : JsonSerializerContext;
