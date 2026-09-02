using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Pins the auto-prepare seam's contract: a turnkey default that never overrides an explicit
/// consumer setting, because the consumer is the only party who knows whether a
/// transaction-pooling proxy sits between them and the database.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/WhizbangConnectionTuning.cs</code-under-test>
public class WhizbangConnectionTuningTests {

  [Test]
  public async Task EnableAutoPrepare_SetsTheDefaults_WhenUnconfiguredAsync() {
    var builder = new NpgsqlDataSourceBuilder("Host=localhost;Database=x;Username=u");

    builder.EnableAutoPrepare();

    await Assert.That(builder.ConnectionStringBuilder.MaxAutoPrepare)
      .IsEqualTo(WhizbangConnectionTuning.DefaultMaxAutoPrepare);
    await Assert.That(builder.ConnectionStringBuilder.AutoPrepareMinUsages)
      .IsEqualTo(WhizbangConnectionTuning.DefaultAutoPrepareMinUsages);
  }

  [Test]
  public async Task EnableAutoPrepare_NeverOverridesAnExplicitConsumerSettingAsync() {
    // A consumer who set Max Auto Prepare chose it deliberately — possibly to a value tuned for
    // their pooler. A turnkey default that overwrote it would be the meter-list failure in reverse.
    var builder = new NpgsqlDataSourceBuilder(
      "Host=localhost;Database=x;Username=u;Max Auto Prepare=5;Auto Prepare Min Usages=9");

    builder.EnableAutoPrepare();

    await Assert.That(builder.ConnectionStringBuilder.MaxAutoPrepare).IsEqualTo(5);
    await Assert.That(builder.ConnectionStringBuilder.AutoPrepareMinUsages).IsEqualTo(9);
  }

  [Test]
  public async Task EnableAutoPrepare_ReturnsTheSameBuilder_ForChainingAsync() {
    var builder = new NpgsqlDataSourceBuilder("Host=localhost;Database=x;Username=u");

    await Assert.That(builder.EnableAutoPrepare()).IsSameReferenceAs(builder);
  }

  [Test]
  public void EnableAutoPrepare_RejectsNonPositiveTuningAsync() {
    var builder = new NpgsqlDataSourceBuilder("Host=localhost;Database=x;Username=u");

    Assert.Throws<ArgumentOutOfRangeException>(() => builder.EnableAutoPrepare(maxAutoPrepare: 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => builder.EnableAutoPrepare(autoPrepareMinUsages: 0));
  }
}
