// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Declares that an event of another stream changes a set of this perspective's rows: the rows whose key column
/// (<see cref="Targets"/>) holds the event's stream id, changed by the set-based update <see cref="Set"/> returns.
/// </summary>
/// <remarks>
/// <para>
/// A collective event is a set update its producer declares. A fan-out is the same set update declared by the
/// consuming perspective, so the producer writes nothing new: "when <c>CustomerRenamed</c> arrives, the order rows
/// whose <c>CustomerId</c> is that customer take the new name." The foreign aggregate's own events are the broadcast.
/// </para>
/// <para>
/// The build checks the declaration: <see cref="Targets"/> names one property of the model with
/// <see cref="FanOut.By{TModel}"/> (WHIZ124); the key is a <see cref="PhysicalFieldAttribute"/> (WHIZ125) with an
/// <see cref="IndexedAttribute"/> (WHIZ126), of a type that holds a stream id (WHIZ127); no set-based apply assigns the
/// key (WHIZ128), because the key changes only through the row's own events; and <see cref="Set"/> depends on nothing
/// but the row and the event (WHIZ106). The generator registers every declaration it accepts in the
/// <see cref="IPerspectiveFanOutRegistry"/> the host serves.
/// </para>
/// <para>
/// A perspective that fans out several events by one key declares <see cref="Targets"/> once; one that needs a
/// different key per event implements it explicitly per interface.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The perspective's model: the rows the event targets.</typeparam>
/// <typeparam name="TEvent">The other stream's event.</typeparam>
/// <example>
/// <code>
/// public class OrderSummaryProjection
///   : IPerspectiveFor&lt;OrderSummary, OrderPlaced&gt;,
///     IPerspectiveFanOutFor&lt;OrderSummary, CustomerRenamed&gt; {
///   public static FanOutKey&lt;OrderSummary&gt; Targets =&gt; FanOut.By&lt;OrderSummary&gt;(o =&gt; o.CustomerId);
///
///   public ICollectiveSpec&lt;OrderSummary&gt; Set(CustomerRenamed e) =&gt;
///     new CollectiveSpec&lt;OrderSummary&gt;(s =&gt; s.SetProperty(o =&gt; o.CustomerName, e.Name));
///   // ...
/// }
/// </code>
/// </example>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-from-another-stream</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Perspectives/PerspectiveFanOutContractTests.cs:Targets_IsReadThroughTheInterfaceAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveFanOutDiscoveryGeneratorTests.cs</tests>
public interface IPerspectiveFanOutFor<TModel, TEvent>
    where TModel : class
    where TEvent : IEvent {
  /// <summary>
  /// The rows an event targets: those whose key column, named with <see cref="FanOut.By{TModel}"/>, equals the event's
  /// stream id. Read by the generator at build time.
  /// </summary>
  static abstract FanOutKey<TModel> Targets { get; }

  /// <summary>
  /// The set-based change <paramref name="eventData"/> makes to every row it targets. It must not assign the key, and it
  /// depends on nothing but the row and the event.
  /// </summary>
  /// <param name="eventData">The other stream's event.</param>
  /// <returns>The setters, as a collective apply returns them.</returns>
  [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "Set is the decided name of the fan-out member (#2275); perspectives are written in C#, where it is not a keyword")]
  ICollectiveSpec<TModel> Set(TEvent eventData);
}
