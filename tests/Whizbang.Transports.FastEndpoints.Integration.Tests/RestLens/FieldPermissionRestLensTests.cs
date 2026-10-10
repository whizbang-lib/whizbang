// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Transports.FastEndpoints.Integration.Tests.RestLens.Generated;

namespace Whizbang.Transports.FastEndpoints.Integration.Tests.RestLens;

/// <summary>
/// The generated REST lens endpoint for a model with <c>[FieldPermission]</c> members, compiled and run, with
/// the response read back as the JSON a client receives: protected members are masked for a caller without
/// the permission, and cannot be used to filter or sort.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.FastEndpoints.Generators/RestLensEndpointGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.FastEndpoints/Security/FieldPermissionJson.cs</code-under-test>
/// <docs>fundamentals/security/security#column-level-security</docs>
[Category("Integration")]
[Category("FastEndpoints")]
[NotInParallel("FastEndpointsSerializerOptions")]
public class FieldPermissionRestLensTests {
  private static async Task<InMemoryPatientLens> _lensAsync() {
    var db = new PatientDbContext(new DbContextOptionsBuilder<PatientDbContext>()
      .UseInMemoryDatabase($"patients-{Guid.NewGuid():N}")
      .Options);
    db.Add(new PerspectiveRow<PatientRecord> {
      Id = Guid.Empty,
      Data = new PatientRecord {
        Id = Guid.Empty,
        Name = "Pat",
        Ssn = "123-45-6789",
        Email = "pat@example.com",
        Phone = "555-0100",
        TaxId = "TX-998877",
        Balance = 1200.5m,
        Address = new PatientAddress { City = "Springfield", Street = "742 Evergreen" },
      },
      Metadata = new PerspectiveMetadata { EventType = "PatientAdmitted", EventId = "e-1", Timestamp = DateTime.UnixEpoch },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UnixEpoch,
      UpdatedAt = DateTime.UnixEpoch,
      Version = 1,
    });
    await db.SaveChangesAsync();
    return new InMemoryPatientLens(db);
  }

  private static ImmutableScopeContext _scopeWith(params string[] permissions) =>
    new(new SecurityExtraction {
      Scope = new PerspectiveScope { TenantId = "tenant-1" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(permissions.Select(p => new Permission(p))),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test",
    }, shouldPropagate: true);

  /// <summary>Runs the endpoint as the caller and returns the first row of the JSON it wrote.</summary>
  private static async Task<JsonElement> _firstRowAsync(IScopeContext? caller, LensRequest? request = null, Action<IServiceCollection>? services = null) {
    var body = new MemoryStream();
    var endpoint = Factory.Create<PatientLensEndpoint>(ctx => {
      ctx.Response.Body = body;
      ctx.AddTestServices(services ?? (s => s.AddWhizbangLenses()));
    }, await _lensAsync());

    ScopeContextAccessor.CurrentContext = caller;
    await endpoint.HandleAsync(request ?? new LensRequest(), CancellationToken.None);

    using var document = JsonDocument.Parse(Encoding.UTF8.GetString(body.ToArray()));
    return document.RootElement.GetProperty("data")[0].Clone();
  }

  [Test]
  public async Task CallerWithoutThePermission_SeesEachStrategysMaskAsync() {
    var row = await _firstRowAsync(_scopeWith("orders:read"));

    await Assert.That(row.GetProperty("name").GetString()).IsEqualTo("Pat").Because("an unprotected member is returned as stored");
    await Assert.That(row.GetProperty("ssn").GetString()).IsEqualTo("****6789").Because("Partial shows the last four characters");
    await Assert.That(row.GetProperty("email").ValueKind).IsEqualTo(JsonValueKind.Null).Because("Hide writes null");
    await Assert.That(row.GetProperty("phone").GetString()).IsEqualTo("****");
    await Assert.That(row.GetProperty("tax_id").GetString()).IsEqualTo("[REDACTED]").Because("an explicit JSON name is masked under that name");
    await Assert.That(row.TryGetProperty("balance", out _)).IsFalse()
      .Because("a non-string member is hidden whatever its strategy, and a decimal has no null so it is left out");
    await Assert.That(row.GetProperty("address").GetProperty("city").GetString()).IsEqualTo("Springfield");
    await Assert.That(row.GetProperty("address").GetProperty("street").GetString()).IsEqualTo("****").Because("a nested type is masked too");
  }

  [Test]
  public async Task CallerWithThePermissions_SeesTheStoredValuesAsync() {
    var row = await _firstRowAsync(_scopeWith("pii:view", "finance:view"));

    await Assert.That(row.GetProperty("ssn").GetString()).IsEqualTo("123-45-6789");
    await Assert.That(row.GetProperty("email").GetString()).IsEqualTo("pat@example.com");
    await Assert.That(row.GetProperty("phone").GetString()).IsEqualTo("555-0100");
    await Assert.That(row.GetProperty("tax_id").GetString()).IsEqualTo("TX-998877");
    await Assert.That(row.GetProperty("balance").GetDecimal()).IsEqualTo(1200.5m);
    await Assert.That(row.GetProperty("address").GetProperty("street").GetString()).IsEqualTo("742 Evergreen");
  }

  [Test]
  public async Task RequestWithNoScope_IsMaskedAsync() {
    var row = await _firstRowAsync(caller: null);

    await Assert.That(row.GetProperty("ssn").GetString()).IsEqualTo("****6789").Because("a request with no scope holds no permissions");
    await Assert.That(row.GetProperty("email").ValueKind).IsEqualTo(JsonValueKind.Null);
  }

  [Test]
  [Arguments("ssn", "123-45-6789")]
  [Arguments("email", "pat@example.com")]
  [Arguments("taxid", "TX-998877")]
  [Arguments("balance", "1200.5")]
  public async Task FilterByAProtectedMember_IsABadRequestAsync(string field, string value) {
    await Assert.That(async () => await _firstRowAsync(_scopeWith(), new LensRequest { Filter = new() { [field] = value } }))
      .Throws<ValidationFailureException>()
      .Because("a filter on a protected member would reveal its value one guess at a time");
  }

  [Test]
  [Arguments("ssn")]
  [Arguments("-balance")]
  public async Task SortByAProtectedMember_IsABadRequestAsync(string sort) {
    await Assert.That(async () => await _firstRowAsync(_scopeWith(), new LensRequest { Sort = sort }))
      .Throws<ValidationFailureException>()
      .Because("an ordering by a protected member would reveal its value");
  }

  [Test]
  public async Task FilterByAnUnprotectedMember_StillWorksAsync() {
    var row = await _firstRowAsync(_scopeWith(), new LensRequest { Filter = new() { ["name"] = "Pat" }, Sort = "-name" });

    await Assert.That(row.GetProperty("name").GetString()).IsEqualTo("Pat");
    await Assert.That(row.GetProperty("ssn").GetString()).IsEqualTo("****6789");
  }

  [Test]
  public async Task SerializerWithoutTheMasking_RefusesToRespondAsync() {
    // The host's JSON options without AddWhizbangLenses: the endpoint fails closed rather than sending
    // protected members unmasked.
    await Assert.That(async () => await _firstRowAsync(_scopeWith(), services: s => s.AddOptions().Configure<JsonOptions>(_ => { })))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("AddWhizbangLenses");
  }
}
