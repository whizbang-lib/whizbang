// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core;

/// <summary>
/// Marks a property or parameter as the stream identifier for event sourcing.
/// Used on both events and commands to identify which stream (aggregate) they belong to.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to a property or record parameter in your message types
/// to identify the stream (aggregate/entity) that the message is associated with.
/// </para>
/// <para>
/// Example with record parameter:
/// <code>
/// public record OrderCreated([property: StreamId] Guid OrderId, string ProductName) : IEvent;
/// </code>
/// </para>
/// <para>
/// Example with property:
/// <code>
/// public record CreateOrder : ICommand {
///   [StreamId]
///   public Guid OrderId { get; init; }
///   public string ProductName { get; init; }
/// }
/// </code>
/// </para>
/// <para>
/// The source generator will discover properties marked with [StreamId]
/// and generate compile-time extractor methods for zero-reflection stream ID resolution.
/// </para>
/// <para>
/// Requirements:
/// - Any property type works: the extractor keys the stream by the value's string form
/// - Only one property per message type should have this attribute (WHIZ006)
/// - Attribute is inherited by derived message types; a derived type replaces an inherited one with
///   <see cref="Override"/>
/// </para>
/// </remarks>
/// <docs>extending/attributes/streamid</docs>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class StreamIdAttribute : Attribute {
  /// <summary>
  /// Marks this stream id as replacing the <c>[StreamId]</c> the type inherits, on purpose. Defaults to
  /// <see langword="false"/>, so a plain <c>[StreamId]</c> is unchanged.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A base command or event often declares a default stream id that a derived type replaces with its own key. With
  /// <c>Override = true</c> on the derived property or record primary-constructor parameter, that key is the stream
  /// id, ahead of any inherited <c>[StreamId]</c> (including where an inherited property would otherwise beat a
  /// derived record's parameter), and hiding the inherited one is not reported as WHIZ006.
  /// </para>
  /// <para>
  /// WHIZ006 is still reported for two <c>[StreamId]</c> declared on one type, override or not, and for a hide
  /// without the option. An override where no base type declares a <c>[StreamId]</c> is reported as WHIZ016.
  /// </para>
  /// <code>
  /// public abstract class CommandBase : ICommand {
  ///   [StreamId] public virtual Guid StreamId { get; set; }
  /// }
  ///
  /// public class AcknowledgeAlert : CommandBase {
  ///   [StreamId(Override = true)] public Guid AckId { get; set; }
  /// }
  /// </code>
  /// </remarks>
  /// <docs>extending/attributes/streamid#overriding-an-inherited-stream-id</docs>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyAttributeTests.cs:StreamIdAttribute_Override_DefaultsToFalseAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/StreamKeyAttributeTests.cs:StreamIdAttribute_Override_CanBeSetAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/StreamIdOverrideGeneratorTests.cs:AnEventOverrideParameter_IsTheStreamIdAheadOfAnInheritedPropertyAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/Analyzers/StreamIdAttributeAnalyzerTests.cs:ADerivedOverridePropertyOverAnInheritedOne_IsNotReportedAsync</tests>
  public bool Override { get; init; }
}
