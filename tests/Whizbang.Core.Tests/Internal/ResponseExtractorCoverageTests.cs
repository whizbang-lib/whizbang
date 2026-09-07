using Whizbang.Core.Internal;

namespace Whizbang.Core.Tests.Internal;

/// <summary>
/// Covers the "no match anywhere in the enumerable" fallthrough of
/// <see cref="ResponseExtractor.TryExtractResponse{TResponse}"/> — the sibling
/// <c>ResponseExtractorTests</c> only exercises arrays/lists that DO contain the requested type.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Internal/ResponseExtractor.cs</code-under-test>
public class ResponseExtractorCoverageTests {
  public record OrderCreated : IEvent {
    [StreamId]
    public required string OrderId { get; init; }
  }

  public record InventoryReserved : IEvent {
    [StreamId]
    public required string ProductId { get; init; }
  }

  [Test]
  public async Task TryExtractResponse_ArrayWithNoMatchingType_ReturnsFalseAsync() {
    // An RPC caller asking LocalInvokeAsync for a response type absent from the receptor's
    // returned enumerable must get a clean "not found" — not a default(TResponse) silently
    // masquerading as a real result.
    var array = new IEvent[] {
      new InventoryReserved { ProductId = "ABC" },
    };

    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(array, out var response);

    await Assert.That(success).IsFalse();
    await Assert.That(response).IsNull();
  }

  [Test]
  public async Task TryExtractResponse_EmptyEnumerable_ReturnsFalseAsync() {
    var success = ResponseExtractor.TryExtractResponse<OrderCreated>(Array.Empty<IEvent>(), out var response);

    await Assert.That(success).IsFalse();
    await Assert.That(response).IsNull();
  }
}
