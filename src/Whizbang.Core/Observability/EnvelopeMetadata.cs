using System.Collections.Generic;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Metadata structure for serializing envelope metadata to JSONB.
/// Contains MessageId and Hops - serialized directly by System.Text.Json.
/// Public for AOT source generation, but not intended for external use.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Generated/InfrastructureJsonContextTests.cs:InfrastructureJsonContext_SerializesEnvelopeMetadata_Async</tests>
public sealed class EnvelopeMetadata {
  public required MessageId MessageId { get; init; }
  public required List<MessageHop> Hops { get; init; }
}
