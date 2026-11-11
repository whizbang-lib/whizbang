// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core;

/// <summary>
/// Extracts stream IDs from messages for delivery receipts and routing.
/// Uses [StreamId] attribute on both events and commands.
/// Uses source-generated extractors - zero reflection, AOT compatible.
/// </summary>
/// <docs>fundamentals/messages/delivery-receipts</docs>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs:ExtractStreamId_CommandWithStreamId_ReturnsStreamIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/StreamIdExtractorTests.cs:ExtractStreamId_EventWithStreamIdAttribute_ExtractsStreamIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherDeliveryReceiptTests.cs:SendAsync_WithGeneratedExtractor_StreamIdIsExtractedAsync</tests>
public interface IStreamIdExtractor {
  /// <summary>
  /// Extracts the stream ID from a message.
  /// Uses the [StreamId] attribute to identify the stream property.
  /// </summary>
  /// <param name="message">The message instance</param>
  /// <param name="messageType">The runtime type of the message</param>
  /// <returns>The stream ID if found, otherwise null</returns>
  Guid? ExtractStreamId(object message, Type messageType);

  /// <summary>
  /// Returns the generation policy for a message type based on [GenerateStreamId] attribute.
  /// Used by the Dispatcher to determine if a StreamId should be auto-generated.
  /// </summary>
  /// <param name="message">The message to check for generation policy</param>
  /// <returns>A tuple of (ShouldGenerate, OnlyIfEmpty) indicating the generation policy.
  /// ShouldGenerate=false means no auto-generation (default for events without [GenerateStreamId]).</returns>
  (bool ShouldGenerate, bool OnlyIfEmpty) GetGenerationPolicy(object message) => (false, false);

  /// <summary>
  /// The strong-id type of a message type's <c>[StreamId]</c>, so the stream a cascaded message inherits can be checked
  /// against the id type it came from (#2072).
  /// </summary>
  /// <param name="messageType">The runtime type of the message.</param>
  /// <returns>The <c>[WhizbangId]</c> type of the stream id, or null when the stream id is not a strong id (a plain
  /// <see cref="Guid"/>, for example) or the type is not recognized. The default returns null, which a setter treats
  /// as a plain Guid source.</returns>
  /// <docs>extending/attributes/streamid#inheriting-a-stream-of-another-id-type</docs>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTests.cs:GetStreamIdType_ReturnsTheFirstTypeAnExtractorReportsAsync</tests>
  Type? GetStreamIdType(Type messageType) => null;

  /// <summary>
  /// Sets the stream ID on a message using the [StreamId]-marked property.
  /// Used by the Dispatcher to auto-generate StreamIds without requiring IHasStreamId.
  /// </summary>
  /// <param name="message">The message to set the StreamId on</param>
  /// <param name="streamId">The StreamId value to set</param>
  /// <returns>True if the StreamId was set, false if the message type is not recognized</returns>
  /// <remarks>
  /// The value is treated as coming from a plain <see cref="Guid"/>: the same as
  /// <see cref="SetStreamId(object, Guid, Type?)"/> with no source type.
  /// </remarks>
  bool SetStreamId(object message, Guid streamId) => false;

  /// <summary>
  /// Sets the stream id a cascaded message inherits from the message that caused it, refusing a source of another id
  /// type. A strong-id stream id accepts a source whose stream-id type is null (a plain Guid), its own type, or a type
  /// its <c>[StreamId(InheritFrom = ...)]</c> lists, and takes the value through the id's <c>From</c>. A Guid stream id
  /// accepts any source.
  /// </summary>
  /// <param name="message">The message to set the stream id on.</param>
  /// <param name="streamId">The inherited stream id.</param>
  /// <param name="sourceStreamIdType">The source message's stream-id type, from <see cref="GetStreamIdType"/>; null for a
  /// plain Guid.</param>
  /// <returns>True if the stream id was set; false if the message type is not recognized, the source type is not
  /// accepted, or the id's <c>From</c> refuses the value. The default calls
  /// <see cref="SetStreamId(object, Guid)"/>, so an extractor that knows no strong ids behaves as before.</returns>
  /// <docs>extending/attributes/streamid#inheriting-a-stream-of-another-id-type</docs>
  /// <tests>tests/Whizbang.Core.Tests/Registry/StreamIdExtractorRegistryTests.cs:SetStreamId_WithASourceType_OnAnExtractorThatKnowsNoTypes_UsesItsPlainSetterAsync</tests>
  bool SetStreamId(object message, Guid streamId, Type? sourceStreamIdType) => SetStreamId(message, streamId);
}
