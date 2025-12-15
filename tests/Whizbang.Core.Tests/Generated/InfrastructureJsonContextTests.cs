using System.Text.Json;
using TUnit.Assertions;
using Whizbang.Core.Generated;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Tests for InfrastructureJsonContext - AOT-compatible JSON serialization for infrastructure types.
/// </summary>
[Category("Serialization")]
public class InfrastructureJsonContextTests {

  [Test]
  public async Task InfrastructureJsonContext_SerializesMessageHop_Async() {
    // NOTE: This test needs implementation - track test gaps with grep 'NotImplementedException'
    // Should verify InfrastructureJsonContext can serialize MessageHop
    // Use InfrastructureJsonContext.Default.GetTypeInfo(typeof(MessageHop))
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task InfrastructureJsonContext_SerializesEnvelopeMetadata_Async() {
    // NOTE: This test needs implementation - track test gaps with grep 'NotImplementedException'
    // Should verify InfrastructureJsonContext can serialize EnvelopeMetadata
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task InfrastructureJsonContext_SerializesServiceInstanceInfo_Async() {
    // NOTE: This test needs implementation - track test gaps with grep 'NotImplementedException'
    // Should verify InfrastructureJsonContext can serialize ServiceInstanceInfo
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }

  [Test]
  public async Task InfrastructureJsonContext_IgnoresNullPropertiesWhenSerializing_Async() {
    // NOTE: This test needs implementation - track test gaps with grep 'NotImplementedException'
    // Should verify DefaultIgnoreCondition = WhenWritingNull
    // Serialize MessageHop with some null properties, verify they're omitted from JSON
    throw new NotImplementedException("Test needs implementation - track test gaps with grep 'NotImplementedException'");
  }
}
