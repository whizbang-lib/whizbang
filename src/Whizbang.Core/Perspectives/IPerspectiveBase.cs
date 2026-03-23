using System.ComponentModel;

namespace Whizbang.Core.Perspectives;

#pragma warning disable S2326 // Unused type parameters should be removed
#pragma warning disable S2436 // Reduce the number of type parameters

/// <summary>
/// Base marker interface that declares model and event types for generator scanning.
/// Do not implement directly — use <see cref="IPerspectiveFor{TModel}"/> or
/// <see cref="IPerspectiveWithActionsFor{TModel}"/>.
/// </summary>
/// <typeparam name="TModel">The read model type</typeparam>
/// <docs>fundamentals/perspectives/perspectives</docs>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel> where TModel : class { }

/// <summary>Marker for perspectives handling 1 event type.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent { }

/// <summary>Marker for perspectives handling 2 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent { }

/// <summary>Marker for perspectives handling 3 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent { }

/// <summary>Marker for perspectives handling 4 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent { }

/// <summary>Marker for perspectives handling 5 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent { }

/// <summary>Marker for perspectives handling 6 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent { }

/// <summary>Marker for perspectives handling 7 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent { }

/// <summary>Marker for perspectives handling 8 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent { }

/// <summary>Marker for perspectives handling 9 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent { }

/// <summary>Marker for perspectives handling 10 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent { }

/// <summary>Marker for perspectives handling 11 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent { }

/// <summary>Marker for perspectives handling 12 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent { }

/// <summary>Marker for perspectives handling 13 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent { }

/// <summary>Marker for perspectives handling 14 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent { }

/// <summary>Marker for perspectives handling 15 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent { }

/// <summary>Marker for perspectives handling 16 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent { }

/// <summary>Marker for perspectives handling 17 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent { }

/// <summary>Marker for perspectives handling 18 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent { }

/// <summary>Marker for perspectives handling 19 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent { }

/// <summary>Marker for perspectives handling 20 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent { }

/// <summary>Marker for perspectives handling 21 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent { }

/// <summary>Marker for perspectives handling 22 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent { }

/// <summary>Marker for perspectives handling 23 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent { }

/// <summary>Marker for perspectives handling 24 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent { }

/// <summary>Marker for perspectives handling 25 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent { }

/// <summary>Marker for perspectives handling 26 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent { }

/// <summary>Marker for perspectives handling 27 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent { }

/// <summary>Marker for perspectives handling 28 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent { }

/// <summary>Marker for perspectives handling 29 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent { }

/// <summary>Marker for perspectives handling 30 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent { }

/// <summary>Marker for perspectives handling 31 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent { }

/// <summary>Marker for perspectives handling 32 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent { }

/// <summary>Marker for perspectives handling 33 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent { }

/// <summary>Marker for perspectives handling 34 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent { }

/// <summary>Marker for perspectives handling 35 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent { }

/// <summary>Marker for perspectives handling 36 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent { }

/// <summary>Marker for perspectives handling 37 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent { }

/// <summary>Marker for perspectives handling 38 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent { }

/// <summary>Marker for perspectives handling 39 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent { }

/// <summary>Marker for perspectives handling 40 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent { }

/// <summary>Marker for perspectives handling 41 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent { }

/// <summary>Marker for perspectives handling 42 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent { }

/// <summary>Marker for perspectives handling 43 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent { }

/// <summary>Marker for perspectives handling 44 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent { }

/// <summary>Marker for perspectives handling 45 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent { }

/// <summary>Marker for perspectives handling 46 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent { }

/// <summary>Marker for perspectives handling 47 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent { }

/// <summary>Marker for perspectives handling 48 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent
  where TEvent48 : IEvent { }

/// <summary>Marker for perspectives handling 49 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent
  where TEvent48 : IEvent
  where TEvent49 : IEvent { }

/// <summary>Marker for perspectives handling 50 event types.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPerspectiveBase<TModel, TEvent1, TEvent2, TEvent3, TEvent4, TEvent5, TEvent6, TEvent7, TEvent8, TEvent9, TEvent10, TEvent11, TEvent12, TEvent13, TEvent14, TEvent15, TEvent16, TEvent17, TEvent18, TEvent19, TEvent20, TEvent21, TEvent22, TEvent23, TEvent24, TEvent25, TEvent26, TEvent27, TEvent28, TEvent29, TEvent30, TEvent31, TEvent32, TEvent33, TEvent34, TEvent35, TEvent36, TEvent37, TEvent38, TEvent39, TEvent40, TEvent41, TEvent42, TEvent43, TEvent44, TEvent45, TEvent46, TEvent47, TEvent48, TEvent49, TEvent50> : IPerspectiveBase<TModel>
  where TModel : class
  where TEvent1 : IEvent
  where TEvent2 : IEvent
  where TEvent3 : IEvent
  where TEvent4 : IEvent
  where TEvent5 : IEvent
  where TEvent6 : IEvent
  where TEvent7 : IEvent
  where TEvent8 : IEvent
  where TEvent9 : IEvent
  where TEvent10 : IEvent
  where TEvent11 : IEvent
  where TEvent12 : IEvent
  where TEvent13 : IEvent
  where TEvent14 : IEvent
  where TEvent15 : IEvent
  where TEvent16 : IEvent
  where TEvent17 : IEvent
  where TEvent18 : IEvent
  where TEvent19 : IEvent
  where TEvent20 : IEvent
  where TEvent21 : IEvent
  where TEvent22 : IEvent
  where TEvent23 : IEvent
  where TEvent24 : IEvent
  where TEvent25 : IEvent
  where TEvent26 : IEvent
  where TEvent27 : IEvent
  where TEvent28 : IEvent
  where TEvent29 : IEvent
  where TEvent30 : IEvent
  where TEvent31 : IEvent
  where TEvent32 : IEvent
  where TEvent33 : IEvent
  where TEvent34 : IEvent
  where TEvent35 : IEvent
  where TEvent36 : IEvent
  where TEvent37 : IEvent
  where TEvent38 : IEvent
  where TEvent39 : IEvent
  where TEvent40 : IEvent
  where TEvent41 : IEvent
  where TEvent42 : IEvent
  where TEvent43 : IEvent
  where TEvent44 : IEvent
  where TEvent45 : IEvent
  where TEvent46 : IEvent
  where TEvent47 : IEvent
  where TEvent48 : IEvent
  where TEvent49 : IEvent
  where TEvent50 : IEvent { }

