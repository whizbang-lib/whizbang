using System;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Marks a perspective Apply method as requiring the model to already exist.
/// When applied, the generated runner code will include a null check before
/// calling the Apply method, throwing an <see cref="InvalidOperationException"/>
/// if the current model is null.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage Pattern:</strong>
/// </para>
/// <para>
/// This attribute is useful for Apply methods that handle "update" events where
/// the model must have been created by a prior event. The method parameter can
/// use a non-nullable type to signal this intent.
/// </para>
/// <para>
/// <strong>Generated Behavior:</strong>
/// </para>
/// <para>
/// When the generator encounters a method with this attribute, it produces:
/// <code>
/// if (currentModel == null)
///   throw new InvalidOperationException(
///     "{ModelType} must exist when applying {EventType} in {PerspectiveName}");
/// return perspective.Apply(currentModel, typedEvent);
/// </code>
/// </para>
/// </remarks>
/// <docs>attributes/must-exist</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/MustExistAttributeTests.cs</tests>
/// <example>
/// <para><strong>Typical usage with creation and update events:</strong></para>
/// <code>
/// public class OrderPerspective :
///     IPerspectiveFor&lt;OrderView, OrderCreated&gt;,
///     IPerspectiveFor&lt;OrderView, OrderShipped&gt; {
///
///   // Creation - nullable parameter, handles initial creation
///   public OrderView Apply(OrderView? current, OrderCreated @event) {
///     return new OrderView { OrderId = @event.OrderId, Status = "Created" };
///   }
///
///   // Update - non-nullable parameter, requires existing model
///   [MustExist]
///   public OrderView Apply(OrderView current, OrderShipped @event) {
///     return current with { Status = "Shipped" };
///   }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class MustExistAttribute : Attribute { }
