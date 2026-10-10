// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Transports.FastEndpoints.Tests.Unit;

/// <summary>
/// Tests for <see cref="FieldPermissionJson"/>: the JSON type info modifier that masks registered
/// <c>[FieldPermission]</c> members when a REST response is written. Each test registers its own model type,
/// since the registry is process-wide.
/// </summary>
/// <tests>FieldPermissionJson</tests>
[NotInParallel("FastEndpointsSerializerOptions")]
public class FieldPermissionJsonTests {
  private static ImmutableScopeContext _scopeWith(params string[] permissions) =>
    new(new SecurityExtraction {
      Scope = new PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(permissions.Select(p => new Permission(p))),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test",
    }, shouldPropagate: true);

  private static JsonSerializerOptions _reflectionOptions() => new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

  private static string _serialize<T>(T value, IScopeContext? caller, JsonSerializerOptions? options = null) {
    options ??= _reflectionOptions().AddWhizbangFieldPermissions();
    ScopeContextAccessor.CurrentContext = caller;
    try {
      return JsonSerializer.Serialize(value, options);
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  // ===== Masking =====

  [Test]
  public async Task CallerWithoutThePermission_GetsPlaceholdersAndWithheldMembersAsync() {
    FieldPermissionJson.Register(typeof(MaskedModel), nameof(MaskedModel.Ssn), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Partial), isString: true);
    FieldPermissionJson.Register(typeof(MaskedModel), nameof(MaskedModel.Email), null, new FieldPermissionAttribute("pii:view"), isString: true);
    FieldPermissionJson.Register(typeof(MaskedModel), nameof(MaskedModel.Balance), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Mask), isString: false);

    var json = _serialize(new MaskedModel { Name = "Pat", Ssn = "123-45-6789", Email = "pat@example.com", Balance = 3m }, _scopeWith());

    await Assert.That(json).IsEqualTo("""{"Name":"Pat","Ssn":"****6789","Email":null}""")
      .Because("Partial shows the last four characters, Hide writes null, and a non-string member with no null is left out");
  }

  [Test]
  public async Task CallerWithThePermission_GetsTheStoredValuesAsync() {
    FieldPermissionJson.Register(typeof(PermittedModel), nameof(PermittedModel.Ssn), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Partial), isString: true);
    FieldPermissionJson.Register(typeof(PermittedModel), nameof(PermittedModel.Email), null, new FieldPermissionAttribute("pii:view"), isString: true);

    var json = _serialize(new PermittedModel { Ssn = "123-45-6789", Email = "pat@example.com" }, _scopeWith("pii:view"));

    await Assert.That(json).IsEqualTo("""{"Ssn":"123-45-6789","Email":"pat@example.com"}""");
  }

  [Test]
  public async Task NoScope_IsMaskedAsync() {
    FieldPermissionJson.Register(typeof(UnscopedModel), nameof(UnscopedModel.Ssn), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Redact), isString: true);

    var json = _serialize(new UnscopedModel { Ssn = "123-45-6789" }, caller: null);

    await Assert.That(json).IsEqualTo("""{"Ssn":"[REDACTED]"}""");
  }

  [Test]
  public async Task NamingPolicy_IsAppliedToTheMemberNameAsync() {
    FieldPermissionJson.Register(typeof(CamelModel), nameof(CamelModel.TaxNumber), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Mask), isString: true);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.AddWhizbangFieldPermissions();

    var json = _serialize(new CamelModel { TaxNumber = "TX-1" }, _scopeWith(), options);

    await Assert.That(json).IsEqualTo("""{"taxNumber":"****"}""");
  }

  [Test]
  public async Task ExplicitJsonName_IsMatchedAsIsAsync() {
    FieldPermissionJson.Register(typeof(RenamedModel), nameof(RenamedModel.TaxNumber), "tax_id", new FieldPermissionAttribute("pii:view", MaskingStrategy.Mask), isString: true);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.AddWhizbangFieldPermissions();

    var json = _serialize(new RenamedModel { TaxNumber = "TX-1" }, _scopeWith(), options);

    await Assert.That(json).IsEqualTo("""{"tax_id":"****"}""");
  }

  [Test]
  public async Task InheritedMember_IsMaskedOnTheDerivedTypeAsync() {
    FieldPermissionJson.Register(typeof(BaseModel), nameof(BaseModel.Secret), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Mask), isString: true);

    var json = _serialize(new DerivedModel { Secret = "s3cret", Extra = "x" }, _scopeWith());

    await Assert.That(json).Contains("\"Secret\":\"****\"");
    await Assert.That(json).Contains("\"Extra\":\"x\"");
  }

  [Test]
  public async Task HiddenNullableValueTypeMember_IsWrittenAsNullAsync() {
    FieldPermissionJson.Register(typeof(NullableValueModel), nameof(NullableValueModel.Limit), null, new FieldPermissionAttribute("finance:view", MaskingStrategy.Redact), isString: false);

    var json = _serialize(new NullableValueModel { Limit = 5 }, _scopeWith());

    await Assert.That(json).IsEqualTo("""{"Limit":null}""");
  }

  [Test]
  public async Task HiddenValueTypeMember_KeepsAnEarlierWriteRuleForAPermittedCallerAsync() {
    // An earlier modifier decided when the member is written; the masking adds to that decision.
    FieldPermissionJson.Register(typeof(RuledValueModel), nameof(RuledValueModel.Count), null, new FieldPermissionAttribute("finance:view"), isString: false);
    var options = new JsonSerializerOptions {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(info => {
        foreach (var property in info.Properties.Where(p => p.Name == nameof(RuledValueModel.Count))) {
          property.ShouldSerialize = (_, value) => value is not 0;
        }
      }),
    }.AddWhizbangFieldPermissions();

    var permittedZero = _serialize(new RuledValueModel { Count = 0 }, _scopeWith("finance:view"), options);
    var permittedValue = _serialize(new RuledValueModel { Count = 3 }, _scopeWith("finance:view"), options);
    var deniedValue = _serialize(new RuledValueModel { Count = 3 }, _scopeWith(), options);

    await Assert.That(permittedZero).IsEqualTo("{}");
    await Assert.That(permittedValue).IsEqualTo("""{"Count":3}""");
    await Assert.That(deniedValue).IsEqualTo("{}");
  }

  [Test]
  public async Task ExistingIgnoreCondition_IsStillHonoredForAPermittedCallerAsync() {
    FieldPermissionJson.Register(typeof(IgnoreNullModel), nameof(IgnoreNullModel.Email), null, new FieldPermissionAttribute("pii:view"), isString: true);
    var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.AddWhizbangFieldPermissions();

    var permittedNull = _serialize(new IgnoreNullModel { Email = null }, _scopeWith("pii:view"), options);
    var permittedValue = _serialize(new IgnoreNullModel { Email = "pat@example.com" }, _scopeWith("pii:view"), options);

    var denied = _serialize(new IgnoreNullModel { Email = "pat@example.com" }, _scopeWith(), options);

    await Assert.That(permittedNull).IsEqualTo("{}").Because("the options' own rule leaves a null out, permission or not");
    await Assert.That(permittedValue).IsEqualTo("""{"Email":"pat@example.com"}""");
    await Assert.That(denied).IsEqualTo("{}").Because("a hidden value is null, which the same rule leaves out");
  }

  [Test]
  public async Task RegisteringAMemberAgain_ReplacesItsPermissionAsync() {
    FieldPermissionJson.Register(typeof(ReplacedModel), nameof(ReplacedModel.Ssn), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Mask), isString: true);
    FieldPermissionJson.Register(typeof(ReplacedModel), nameof(ReplacedModel.Ssn), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Redact), isString: true);

    var json = _serialize(new ReplacedModel { Ssn = "123-45-6789" }, _scopeWith());

    await Assert.That(json).IsEqualTo("""{"Ssn":"[REDACTED]"}""");
  }

  [Test]
  public async Task UnregisteredTypesAndValues_AreUntouchedAsync() {
    var json = _serialize(new UnregisteredModel { Ssn = "123-45-6789", Tags = ["a"] }, _scopeWith());

    await Assert.That(json).IsEqualTo("""{"Ssn":"123-45-6789","Tags":["a"]}""");
  }

  [Test]
  public async Task AddWhizbangFieldPermissions_KeepsTheExistingResolverAsync() {
    FieldPermissionJson.Register(typeof(ContextModel), nameof(ContextModel.Ssn), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Mask), isString: true);
    var options = new JsonSerializerOptions { TypeInfoResolver = FieldPermissionJsonTestContext.Default }.AddWhizbangFieldPermissions();

    var json = _serialize(new ContextModel { Ssn = "123-45-6789" }, _scopeWith(), options);

    await Assert.That(json).IsEqualTo("""{"Ssn":"****"}""").Because("a source-generated context keeps serving the contract, with the masking added");
  }

  [Test]
  public async Task AddWhizbangFieldPermissions_OptionsWithoutAResolver_ThrowsAsync() {
    await Assert.That(() => new JsonSerializerOptions().AddWhizbangFieldPermissions())
      .Throws<InvalidOperationException>()
      .WithMessageContaining("TypeInfoResolver");
  }

  // ===== EnsureMasked =====

  [Test]
  public async Task EnsureMasked_OptionsWithTheMasking_PassesAsync() {
    var options = _reflectionOptions().AddWhizbangFieldPermissions();

    await Assert.That(() => FieldPermissionJson.EnsureMasked(options, typeof(UnregisteredModel))).ThrowsNothing();
  }

  [Test]
  public async Task EnsureMasked_OptionsWithoutTheMasking_ThrowsAsync() {
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    await Assert.That(() => FieldPermissionJson.EnsureMasked(options, typeof(UnregisteredModel)))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("AddWhizbangLenses");
  }

  // ===== Registration through AddWhizbangLenses =====

  [Test]
  public async Task AddWhizbangLenses_AddsTheMaskingToTheHostJsonOptionsOnceAsync() {
    FieldPermissionJson.Register(typeof(HostModel), nameof(HostModel.Ssn), null, new FieldPermissionAttribute("pii:view", MaskingStrategy.Partial), isString: true);
    var services = new ServiceCollection();
    services.AddWhizbangLenses();
    services.AddWhizbangLenses();
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
    var json = _serialize(new HostModel { Ssn = "123-45-6789" }, _scopeWith(), options);

    await Assert.That(json).IsEqualTo("""{"ssn":"****6789"}""");
    await Assert.That(() => FieldPermissionJson.EnsureMasked(options, typeof(HostModel))).ThrowsNothing();
    await Assert.That(services.Count(d => d.ServiceType == typeof(IPostConfigureOptions<JsonOptions>))).IsEqualTo(1);
  }

  [Test]
  public async Task EnsureMasked_ForTheFastEndpointsSerializer_ReadsTheOptionsItWritesWithAsync() {
    // FastEndpoints takes its serializer options from the host's JSON options; the single-argument overload
    // checks those.
    global::FastEndpoints.Factory.RegisterTestServices(s => s.AddWhizbangLenses());
    await Assert.That(() => FieldPermissionJson.EnsureMasked(typeof(UnregisteredModel))).ThrowsNothing();

    global::FastEndpoints.Factory.RegisterTestServices(s => s.AddOptions().Configure<JsonOptions>(_ => { }));
    await Assert.That(() => FieldPermissionJson.EnsureMasked(typeof(UnregisteredModel))).Throws<InvalidOperationException>();
  }

  // ===== Models (one per test, since the registry is process-wide) =====

  public sealed class MaskedModel {
    public string? Name { get; set; }
    public string? Ssn { get; set; }
    public string? Email { get; set; }
    public decimal Balance { get; set; }
  }

  public sealed class PermittedModel {
    public string? Ssn { get; set; }
    public string? Email { get; set; }
  }

  public sealed class UnscopedModel {
    public string? Ssn { get; set; }
  }

  public sealed class CamelModel {
    public string? TaxNumber { get; set; }
  }

  public sealed class RenamedModel {
    [JsonPropertyName("tax_id")]
    public string? TaxNumber { get; set; }
  }

  public class BaseModel {
    public string? Secret { get; set; }
  }

  public sealed class DerivedModel : BaseModel {
    public string? Extra { get; set; }
  }

  public sealed class IgnoreNullModel {
    public string? Email { get; set; }
  }

  public sealed class NullableValueModel {
    public int? Limit { get; set; }
  }

  public sealed class RuledValueModel {
    public int Count { get; set; }
  }

  public sealed class ReplacedModel {
    public string? Ssn { get; set; }
  }

  public sealed class UnregisteredModel {
    public string? Ssn { get; set; }
    public List<string> Tags { get; set; } = [];
  }

  public sealed class ContextModel {
    public string? Ssn { get; set; }
  }

  public sealed class HostModel {
    public string? Ssn { get; set; }
  }
}

/// <summary>A source-generated contract for <see cref="FieldPermissionJsonTests.ContextModel"/>.</summary>
[JsonSerializable(typeof(FieldPermissionJsonTests.ContextModel))]
internal sealed partial class FieldPermissionJsonTestContext : JsonSerializerContext;
