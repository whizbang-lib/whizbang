// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #1548: the driver-neutral schema generator emits the perspective registry payload the EF Core driver has
/// always written, and registers it at module load, so the Dapper driver can reconcile <c>wh_perspective_registry</c>
/// at startup. Without it, row retention on Dapper found no registered perspective and enrolled nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/PerspectiveSchemaGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators.Shared/Utilities/PerspectiveRegistryPayload.cs</code-under-test>
public partial class PerspectiveSchemaGeneratorRegistryTests {

  private const string NESTED_MODEL_SOURCE = """
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace MyApp;

    public record OwnedEvent : IEvent;

    public static class Owner {
      public record Model {
        public string Id { get; init; } = "";
      }
    }

    public static class Holder {
      public class OwnerPerspective : IPerspectiveFor<Owner.Model, OwnedEvent> {
        public Owner.Model Apply(Owner.Model currentData, OwnedEvent eventData) => currentData;
      }
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NestedModel_EmitsTheRegistryPayloadInClrFormAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(NESTED_MODEL_SOURCE);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    var registryLine = _line(generated, "RegistryJson =");

    await Assert.That(registryLine).Contains("ClrTypeName\\\":\\\"MyApp.Owner+Model\\\"")
      .Because("the registry key is the model's CLR form, '+' for the nesting, the form the runtime looks it up by");
    await Assert.That(registryLine).Contains("PerspectiveName\\\":\\\"MyApp.Holder+OwnerPerspective\\\"")
      .Because("the perspective is named in the CLR form its cursors and snapshots carry");
    await Assert.That(registryLine).Contains("ServiceName\\\":\\\"TestAssembly\\\"")
      .Because("the service is the assembly that declares the perspective, as on the EF Core driver");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistersThePayloadAtModuleLoadAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(NESTED_MODEL_SOURCE);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(generated).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(generated)
      .Contains("global::Whizbang.Core.Perspectives.PerspectiveRegistryPayloads.Register(ServiceName, RegistryJson);")
      .Because("the Dapper driver reconciles what the host's assemblies registered, with no registration code of its own");
    await Assert.That(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_UnnamedAssembly_NamesTheServiceUnknownAsync() {
    var result = GeneratorTestHelper.RunGeneratorOnUnnamedAssembly(new PerspectiveSchemaGenerator(), NESTED_MODEL_SOURCE);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    await Assert.That(_line(generated, "RegistryJson =")).Contains("ServiceName\\\":\\\"Unknown\\\"")
      .Because("the EF Core driver names an unnamed assembly's service the same way");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistryTable_IsTheTableTheDdlCreatesAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(NESTED_MODEL_SOURCE);

    var generated = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");
    var table = _tableName().Match(_line(generated, "RegistryJson =")).Groups[1].Value;

    await Assert.That(table).IsNotEmpty();
    await Assert.That(generated).Contains($"CREATE TABLE IF NOT EXISTS {table} (")
      .Because("the retention sweeps evict from the registry row's table, so it has to be the one this driver builds");
  }

  private static string _line(string? source, string marker) =>
    source!.Split('\n').First(l => l.Contains(marker, StringComparison.Ordinal));

  [GeneratedRegex("TableName\\\\\":\\\\\"([a-z0-9_]+)\\\\\"")]
  private static partial Regex _tableName();
}
