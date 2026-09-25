using System.Linq.Expressions;
using System.Text.Json;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Dapper.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// How the Dapper compiler hands an <c>UpsertElement</c> to the shared upsert expression. The behavior is
/// proven against a real database in <c>DapperCollectiveApplierIntegrationTests</c>; these lock the shape
/// and the refusals.
/// </summary>
public class DapperCollectiveUpsertElementCompilerTests {

  private static readonly JsonSerializerOptions _jsonOptions = new();

  public sealed class Cell {
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
  }

  public sealed class Model {
    public List<Cell> Cells { get; set; } = [];
  }

  private sealed record Spec(Expression<Action<ICollectiveSetters<Model>>> Setters) : ICollectiveSpec<Model> {
    public Expression<Func<Whizbang.Core.Lenses.PerspectiveRow<Model>, bool>>? Where => null;
  }

  [Test]
  public async Task Compile_UpsertElement_AssignsTheArrayFromTheSharedExpressionAsync() {
    var cell = new Cell { Key = "k1", Value = "v1" };

    var compiled = DapperCollectiveSpecCompiler<Model>.Compile(
      new Spec(s => s.UpsertElement(m => m.Cells, c => c.Key, cell)), _jsonOptions);

    await Assert.That(compiled.SqlFragment).StartsWith("data = jsonb_set(data, '{Cells}', CASE WHEN jsonb_typeof(data->'Cells') = 'array'");
    var param = compiled.Parameters.Single();
    await Assert.That(compiled.SqlFragment).Contains($"@{param.Key}::jsonb");
    await Assert.That(param.Value).IsEqualTo("""{"Key":"k1","Value":"v1"}""");
  }

  [Test]
  public async Task Compile_UpsertElement_ANestedKey_IsRefusedAsync() {
    var cell = new Cell { Key = "k1", Value = "v1" };

    await Assert.That(() => DapperCollectiveSpecCompiler<Model>.Compile(
      new Spec(s => s.UpsertElement(m => m.Cells, c => c.Key.Length, cell)), _jsonOptions)).Throws<NotSupportedException>();
  }

  [Test]
  public async Task Compile_UpsertElement_ANullElement_IsRefusedAsync() {
    Cell? cell = null;

    await Assert.That(() => DapperCollectiveSpecCompiler<Model>.Compile(
      new Spec(s => s.UpsertElement(m => m.Cells, c => c.Key, cell!)), _jsonOptions)).Throws<ArgumentException>();
  }
}
