namespace Whizbang.Core.Dispatch;

/// <summary>
/// Combines a typed business result with a delivery receipt from LocalInvoke.
/// Bridges the gap between LocalInvokeAsync (typed result only) and SendAsync (receipt only).
/// </summary>
/// <typeparam name="TResult">The typed business result from the receptor.</typeparam>
/// <param name="Value">The business result returned by the receptor.</param>
/// <param name="Receipt">The delivery receipt with dispatch metadata (MessageId, StreamId, CorrelationId, etc.).</param>
/// <docs>core-concepts/invoke-result</docs>
public sealed record InvokeResult<TResult>(TResult Value, IDeliveryReceipt Receipt);
