namespace Whizbang.Core.Perspectives;

/// <summary>One promoted column of a <see cref="FieldStorageMode.Split"/> model.</summary>
/// <param name="Name">The column name.</param>
/// <param name="IsVector">Whether the column holds a vector, which a store reads back as its components.</param>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public readonly record struct SplitPhysicalColumn(string Name, bool IsVector);

/// <summary>
/// The promoted columns of a <see cref="FieldStorageMode.Split"/> model and the generated code that copies
/// them into a model loaded from its document.
/// </summary>
/// <typeparam name="TModel">The perspective's read model.</typeparam>
/// <remarks>
/// Emitted by the perspective runner generator, so the copy is plain property assignment (a <c>with</c>
/// expression for a record) and needs no reflection.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/SplitPhysicalFieldRegistryTests.cs</tests>
public sealed class SplitPhysicalFieldMap<TModel> where TModel : class {
  private readonly Func<TModel, IPhysicalColumnReader, TModel> _hydrate;

  /// <summary>Creates the map for one model.</summary>
  /// <param name="columns">Every promoted column, in the order a store should read them.</param>
  /// <param name="hydrate">Copies the columns into a model and returns it, or the copy a record makes.</param>
  public SplitPhysicalFieldMap(IReadOnlyList<SplitPhysicalColumn> columns, Func<TModel, IPhysicalColumnReader, TModel> hydrate) {
    ArgumentNullException.ThrowIfNull(columns);
    ArgumentNullException.ThrowIfNull(hydrate);
    Columns = columns;
    _hydrate = hydrate;
  }

  /// <summary>Every promoted column, in the order a store should read them.</summary>
  public IReadOnlyList<SplitPhysicalColumn> Columns { get; }

  /// <summary>Returns <paramref name="model"/> with every promoted property set from its column.</summary>
  /// <param name="model">The model as the document held it.</param>
  /// <param name="reader">The row's promoted columns.</param>
  public TModel Hydrate(TModel model, IPhysicalColumnReader reader) => _hydrate(model, reader);
}
