using Whizbang.Core.Lenses;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Extends <see cref="IEventStoreQuery"/> with scope filter application capability.
/// Implementations receive scope filters from <see cref="IScopedLensFactory"/>
/// and apply them to query results.
/// </summary>
/// <remarks>
/// <para>
/// This interface combines <see cref="IEventStoreQuery"/> for raw event querying
/// with <see cref="IFilterableLens"/> for automatic scope filtering.
/// </para>
/// <para>
/// When resolved via <see cref="IScopedLensFactory"/>, the factory automatically
/// calls <see cref="IFilterableLens.ApplyFilter"/> with the appropriate scope context.
/// </para>
/// </remarks>
/// <docs>core-concepts/event-store-query</docs>
/// <tests>Whizbang.Core.Tests/Messaging/IFilterableEventStoreQueryTests.cs</tests>
public interface IFilterableEventStoreQuery : IEventStoreQuery, IFilterableLens { }
