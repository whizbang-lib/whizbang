// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Transports.HotChocolate.Tests.Fixtures;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="FieldPermissionTypeInterceptor"/>, which <c>AddWhizbangLenses()</c> registers: every
/// object type field backed by a <c>[FieldPermission]</c> property returns the masked value to a caller
/// without the permission, and no such property appears in a filter or sort input type.
/// </summary>
/// <tests>FieldPermissionTypeInterceptor</tests>
public class FieldPermissionTypeInterceptorTests {
  private const string PATIENT_FIELDS = "name ssn email phone notes balance address { city street }";

  private static TestScopeContext _scopeWith(params string[] permissions) =>
    new() { Permissions = new HashSet<Permission>(permissions.Select(p => new Permission(p))) };

  private static async Task<IRequestExecutor> _executorAsync(IScopeContext? scope, bool registerAccessor = true) {
    var services = new ServiceCollection();
    if (registerAccessor) {
      services.AddSingleton<IScopeContextAccessor>(new TestScopeContextAccessor { Current = scope });
    }
    services
      .AddGraphQLServer()
      .AddWhizbangLenses()
      .AddQueryType<PatientQuery>()
      .AddType<EmployeeType>();
    return await services.BuildServiceProvider().GetRequestExecutorAsync();
  }

  private static async Task<string> _queryAsync(IScopeContext? scope, string query, bool registerAccessor = true) {
    var executor = await _executorAsync(scope, registerAccessor);
    var result = await executor.ExecuteAsync(query);
    return result.ToJson();
  }

  // ===== Output: what each caller sees =====

  [Test]
  public async Task CallerWithoutThePermission_SeesEachStrategysMaskAsync() {
    var json = await _queryAsync(_scopeWith("orders:read"), $"{{ patient {{ {PATIENT_FIELDS} }} }}");

    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("\"name\": \"Pat\"").Because("an unprotected field is returned as stored");
    await Assert.That(json).Contains("\"ssn\": \"****6789\"").Because("Partial shows the last four characters");
    await Assert.That(json).Contains("\"email\": null").Because("Hide returns null");
    await Assert.That(json).Contains("\"phone\": \"****\"").Because("Mask returns the placeholder");
    await Assert.That(json).Contains("\"notes\": \"[REDACTED]\"").Because("Redact returns the redaction marker");
    await Assert.That(json).Contains("\"balance\": null").Because("a non-string member is hidden whatever its strategy");
    await Assert.That(json).Contains("\"city\": \"Springfield\"");
    await Assert.That(json).Contains("\"street\": \"****\"").Because("a nested model type is masked too");
    await Assert.That(json).DoesNotContain("123-45-6789");
    await Assert.That(json).DoesNotContain("pat@example.com");
    await Assert.That(json).DoesNotContain("742 Evergreen");
  }

  [Test]
  public async Task CallerWithThePermissions_SeesTheStoredValuesAsync() {
    var json = await _queryAsync(_scopeWith("pii:view", "finance:view"), $"{{ patient {{ {PATIENT_FIELDS} }} }}");

    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("\"ssn\": \"123-45-6789\"");
    await Assert.That(json).Contains("\"email\": \"pat@example.com\"");
    await Assert.That(json).Contains("\"phone\": \"555-0100\"");
    await Assert.That(json).Contains("\"notes\": \"allergic to penicillin\"");
    await Assert.That(json).Contains("\"balance\": 1200.5");
    await Assert.That(json).Contains("\"street\": \"742 Evergreen\"");
  }

  [Test]
  public async Task EachFieldIsJudgedByItsOwnPermissionAsync() {
    var json = await _queryAsync(_scopeWith("finance:view"), "{ patient { ssn balance } }");

    await Assert.That(json).Contains("\"ssn\": \"****6789\"");
    await Assert.That(json).Contains("\"balance\": 1200.5");
  }

  [Test]
  public async Task RequestWithNoScope_IsMaskedAsync() {
    var json = await _queryAsync(scope: null, "{ patient { ssn } }");

    await Assert.That(json).Contains("\"ssn\": \"****6789\"").Because("a request with no scope holds no permissions");
  }

  [Test]
  public async Task NoScopeAccessorRegistered_IsMaskedAsync() {
    var json = await _queryAsync(_scopeWith("pii:view"), "{ patient { ssn } }", registerAccessor: false);

    await Assert.That(json).Contains("\"ssn\": \"****6789\"").Because("with no way to read the request's scope the field fails closed");
  }

  [Test]
  public async Task LensRowDataField_IsMaskedAsync() {
    var json = await _queryAsync(_scopeWith(), "{ rows { data { ssn name } } }");

    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("\"ssn\": \"****6789\"");
    await Assert.That(json).Contains("\"name\": \"Pat\"");
  }

  [Test]
  public async Task InterfaceAndImplementingType_AreBothMaskedAndNullableAsync() {
    var executor = await _executorAsync(_scopeWith());
    var person = executor.Schema.GetType<InterfaceType>("PersonBase");
    var employee = executor.Schema.GetType<ObjectType>("Employee");

    var json = (await executor.ExecuteAsync("{ person { badge ... on Employee { team } } }")).ToJson();

    await Assert.That(person.Fields["badge"].Type.IsNonNullType()).IsFalse();
    await Assert.That(person.Fields["nickname"].Type.IsNonNullType()).IsTrue();
    await Assert.That(employee.Fields["badge"].Type.IsNonNullType()).IsFalse();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("\"badge\": \"****\"");
    await Assert.That(json).Contains("\"team\": \"platform\"");
  }

  // ===== Schema: protected fields are nullable =====

  [Test]
  public async Task ProtectedFields_AreNullable_UnprotectedFieldsAreUnchangedAsync() {
    var executor = await _executorAsync(_scopeWith());
    var patient = executor.Schema.GetType<ObjectType>("PatientModel");

    await Assert.That(patient.Fields["name"].Type.IsNonNullType()).IsTrue();
    await Assert.That(patient.Fields["ssn"].Type.IsNonNullType()).IsFalse()
      .Because("Hide returns null, so a protected field cannot be non-null");
    await Assert.That(patient.Fields["balance"].Type.IsNonNullType()).IsFalse();
    await Assert.That(patient.Fields["badgeCode"].Type.IsNonNullType()).IsFalse()
      .Because("an explicit non-null GraphQL type on a protected field is made nullable too");
  }

  [Test]
  public async Task ProtectedFieldWithAnExplicitGraphQLType_IsHiddenWithoutAnErrorAsync() {
    var json = await _queryAsync(_scopeWith(), "{ patient { badgeCode alias } }");

    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("\"badgeCode\": null");
    await Assert.That(json).Contains("\"alias\": \"[REDACTED]\"")
      .Because("a protected field whose explicit GraphQL type is already nullable keeps it and is masked");
  }

  // ===== Filter and sort inputs: protected fields are not offered =====

  [Test]
  public async Task FilterInput_LeavesOutProtectedFieldsAsync() {
    var executor = await _executorAsync(_scopeWith());

    var patientFilter = _inputFieldNames(executor.Schema, "PatientModelFilterInput");
    var addressFilter = _inputFieldNames(executor.Schema, "AddressModelFilterInput");

    await Assert.That(patientFilter).Contains("name");
    await Assert.That(patientFilter).Contains("address");
    await Assert.That(patientFilter).DoesNotContain("ssn");
    await Assert.That(patientFilter).DoesNotContain("email");
    await Assert.That(patientFilter).DoesNotContain("phone");
    await Assert.That(patientFilter).DoesNotContain("notes");
    await Assert.That(patientFilter).DoesNotContain("balance");
    await Assert.That(addressFilter).Contains("city");
    await Assert.That(addressFilter).DoesNotContain("street");
  }

  [Test]
  public async Task SortInput_LeavesOutProtectedFieldsAsync() {
    var executor = await _executorAsync(_scopeWith());

    var patientSort = _inputFieldNames(executor.Schema, "PatientModelSortInput");

    await Assert.That(patientSort).Contains("name");
    await Assert.That(patientSort).DoesNotContain("ssn");
    await Assert.That(patientSort).DoesNotContain("balance");
  }

  [Test]
  public async Task FilteringByAProtectedField_IsRejectedAsync() {
    var json = await _queryAsync(_scopeWith(), """{ rows(where: { data: { ssn: { startsWith: "123" } } }) { data { name } } }""");

    await Assert.That(json).Contains("errors")
      .Because("a filter on a protected field would reveal its value one guess at a time");
    await Assert.That(json).DoesNotContain("\"name\": \"Pat\"");
  }

  [Test]
  public async Task SortingByAProtectedField_IsRejectedAsync() {
    var json = await _queryAsync(_scopeWith(), "{ rows(order: { data: { ssn: ASC } }) { data { name } } }");

    await Assert.That(json).Contains("errors");
    await Assert.That(json).DoesNotContain("\"name\": \"Pat\"");
  }

  [Test]
  public async Task FilteringByAnUnprotectedField_StillWorksAsync() {
    var json = await _queryAsync(_scopeWith(), """{ rows(where: { data: { name: { eq: "Pat" } } }) { data { name ssn } } }""");

    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("\"name\": \"Pat\"");
    await Assert.That(json).Contains("\"ssn\": \"****6789\"");
  }

  private static string[] _inputFieldNames(ISchema schema, string typeName) =>
    [.. schema.GetType<InputObjectType>(typeName).Fields.Select(f => f.Name)];
}

/// <summary>A read model with a protected property under each masking strategy.</summary>
public class PatientModel {
  /// <summary>Unprotected.</summary>
  public string Name { get; set; } = "";

  /// <summary>Partial.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Partial)]
  public string Ssn { get; set; } = "";

  /// <summary>Hide (the default).</summary>
  [FieldPermission("pii:view")]
  public string Email { get; set; } = "";

  /// <summary>Mask.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Mask)]
  public string Phone { get; set; } = "";

  /// <summary>Redact.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Redact)]
  public string Notes { get; set; } = "";

  /// <summary>A non-string member under Mask, which hides it.</summary>
  [FieldPermission("finance:view", MaskingStrategy.Mask)]
  public decimal Balance { get; set; }

  /// <summary>Protected, with an explicit non-null GraphQL type.</summary>
  [FieldPermission("pii:view")]
  [GraphQLType("String!")]
  public string BadgeCode { get; set; } = "";

  /// <summary>Protected, with an explicit nullable GraphQL type.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Redact)]
  [GraphQLType("String")]
  public string Alias { get; set; } = "";

  /// <summary>A nested model type with a protected property of its own.</summary>
  public AddressModel Address { get; set; } = new();
}

/// <summary>A nested model type.</summary>
public class AddressModel {
  /// <summary>Unprotected.</summary>
  public string City { get; set; } = "";

  /// <summary>Mask.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Mask)]
  public string Street { get; set; } = "";
}

/// <summary>A polymorphic base class exposed as a GraphQL interface, declaring a protected property.</summary>
public abstract class PersonBase {
  /// <summary>Unprotected.</summary>
  public string Nickname { get; set; } = "";

  /// <summary>Mask.</summary>
  [FieldPermission("pii:view", MaskingStrategy.Mask)]
  public string Badge { get; set; } = "";
}

/// <summary>An implementation of <see cref="PersonBase"/>.</summary>
public class Employee : PersonBase {
  /// <summary>Unprotected.</summary>
  public string Team { get; set; } = "";
}

/// <summary>Exposes <see cref="Employee"/> as an implementation of the <see cref="PersonBase"/> interface.</summary>
public sealed class EmployeeType : ObjectType<Employee> {
  /// <inheritdoc />
  protected override void Configure(IObjectTypeDescriptor<Employee> descriptor) =>
    descriptor.Implements<InterfaceType<PersonBase>>();
}

/// <summary>Query type serving one patient, as a model, as filterable lens rows, and through an interface.</summary>
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "HotChocolate requires instance methods for GraphQL resolvers")]
[SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "HotChocolate requires instance methods for GraphQL resolvers")]
public class PatientQuery {
  private static PatientModel _patient() => new() {
    Name = "Pat",
    Ssn = "123-45-6789",
    Email = "pat@example.com",
    Phone = "555-0100",
    Notes = "allergic to penicillin",
    Balance = 1200.5m,
    BadgeCode = "B-77",
    Alias = "Patty",
    Address = new AddressModel { City = "Springfield", Street = "742 Evergreen" },
  };

  /// <summary>One patient.</summary>
  public PatientModel GetPatient() => _patient();

  /// <summary>Lens rows over the patient, filterable and sortable.</summary>
  [UseFiltering]
  [UseSorting]
  public IQueryable<PerspectiveRow<PatientModel>> GetRows() => new[] {
    new PerspectiveRow<PatientModel> {
      Id = Guid.Empty,
      Data = _patient(),
      Metadata = new PerspectiveMetadata { EventType = "PatientAdmitted", EventId = "e-1", Timestamp = DateTime.UnixEpoch },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UnixEpoch,
      UpdatedAt = DateTime.UnixEpoch,
      Version = 1,
    },
  }.AsQueryable();

  /// <summary>A person, through the interface.</summary>
  [GraphQLType(typeof(InterfaceType<PersonBase>))]
  public PersonBase GetPerson() => new Employee { Badge = "E-42", Team = "platform" };
}
