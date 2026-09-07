using System.Reflection;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Guards the sharding contract: every test class in this assembly must declare exactly one
/// <c>Shard*</c> category.
/// </summary>
/// <remarks>
/// <para>
/// CI splits this project across runners with <c>--treenode-filter "…[Category=ShardN]"</c>. A class
/// carrying no shard category matches no filter, so it runs in NO shard — and the failure mode is
/// silent: every job stays green while the tests simply never execute. Coverage drops and nothing
/// says so. A class carrying TWO shard categories is the opposite waste, running the same tests on
/// two runners.
/// </para>
/// <para>
/// This is the only thing standing between "add a test class" and "quietly stop testing it", so it
/// asserts the whole assembly rather than sampling.
/// </para>
/// </remarks>
[Category("Shard1")]
public class ShardCoverageGuardTests {

  [Test]
  public async Task EveryTestClass_DeclaresExactlyOneShardCategoryAsync() {
    var offenders = typeof(ShardCoverageGuardTests).Assembly.GetTypes()
      .Where(t => t is { IsClass: true, IsAbstract: false })
      .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                   .Any(m => m.GetCustomAttributes(typeof(TestAttribute), inherit: false).Length > 0))
      .Select(t => new {
        Type = t,
        Categories = t.GetCustomAttributes(typeof(CategoryAttribute), inherit: true)
                     .Cast<CategoryAttribute>()
                     .Select(c => c.Category)
                     .ToList()
      })
      .Select(x => new {
        x.Type,
        Shards = x.Categories.Where(c => c.StartsWith("Shard", StringComparison.Ordinal)).ToList(),
        // Benchmark classes run on a scheduled or manual job, never in a PR slice: the category is the
        // alternative to a shard, and a class must carry exactly one of the two.
        IsBenchmark = x.Categories.Contains("Benchmark")
      })
      .Where(x => x.IsBenchmark ? x.Shards.Count != 0 : x.Shards.Count != 1)
      .Select(x => $"{x.Type.FullName} -> [{string.Join(", ", x.Shards)}]")
      .OrderBy(s => s)
      .ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("CI runs this project as [Category=ShardN] slices; a class with no shard category "
             + "runs in NO slice and silently stops being tested, and one with two runs twice. "
             + "A [Category(\"Benchmark\")] class runs on the scheduled benchmark job instead and must carry no shard. "
             + "Offenders listed above - give each exactly one Shard category (or Benchmark alone).");
  }
}
