# AOT Compatibility Requirements

**Zero Reflection - Native AOT from Day One**

Whizbang is designed for **strict AOT compatibility** with zero reflection. This document defines the absolute requirements for library code, sample projects, and test projects.

---

## Table of Contents

1. [Strict Rules by Project Type](#strict-rules-by-project-type)
2. [What Triggers AOT Incompatibility](#what-triggers-aot-incompatibility)
3. [AOT-Compatible Alternatives](#aot-compatible-alternatives)
4. [Verification Process](#verification-process)
5. [Common Mistakes](#common-mistakes)

---

## Strict Rules by Project Type

### Library Code (ABSOLUTE - Zero Tolerance)

**Requirements:**
- ✅ **ZERO reflection** - No exceptions
- ✅ All type discovery via source generators
- ✅ Must compile with `<PublishAot>true</PublishAot>`
- ✅ Must publish as native AOT binary
- ✅ Must use source-generated serialization
- ✅ Must use Vogen for value objects

**Forbidden:**
- ❌ **NEVER** use `Type.GetType()`
- ❌ **NEVER** use `Activator.CreateInstance()`
- ❌ **NEVER** use `Assembly.GetTypes()`
- ❌ **NEVER** use `MethodInfo.Invoke()`
- ❌ **NEVER** use reflection-based serializers
- ❌ **NEVER** use `Expression.Compile()` (runtime compilation)
- ❌ **NEVER** use `DynamicMethod` or dynamic code generation
- ❌ **NEVER** use `MakeGenericType()` or `MakeGenericMethod()`

**Project File Configuration:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- AOT Publishing -->
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>false</InvariantGlobalization>
    <IlcOptimizationPreference>Speed</IlcOptimizationPreference>

    <!-- Trim warnings as errors -->
    <TrimmerSingleWarn>false</TrimmerSingleWarn>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
  </PropertyGroup>
</Project>
```

---

### Sample Projects (ABSOLUTE - Demonstrate End-to-End)

Sample projects exist to **dogfood** the library and demonstrate real-world AOT usage.

**Requirements:**
- ✅ **ZERO reflection** (same as library)
- ✅ Must publish and run as native AOT binary
- ✅ Demonstrate end-to-end AOT workflow
- ✅ Use library patterns (no workarounds)
- ✅ `<PublishAot>true</PublishAot>` in all sample projects

**Purpose:**
- Demonstrate what users will do
- Validate library AOT compatibility
- Showcase best practices
- Prove real-world viability

**Forbidden:**
- ❌ **NO** reflection-based workarounds
- ❌ **NO** hacks that users wouldn't use
- ❌ **NO** bypassing library features
- ❌ **NO** "but it's just a sample" excuses

**If sample needs a feature the library doesn't provide:**
1. ⛔ STOP work on sample
2. 📝 Identify what's needed
3. 🔧 Implement in library first
4. ✅ Add tests to library
5. ✅ THEN use it in sample

**Example Projects:**
```
samples/
└── ECommerce/
    ├── ECommerce.OrderService.API/      # Must be AOT
    ├── ECommerce.BFF.API/               # Must be AOT
    ├── ECommerce.InventoryWorker/       # Must be AOT
    ├── ECommerce.PaymentWorker/         # Must be AOT
    └── ... (all must be AOT compatible)
```

---

### Test Projects (PREFERRED but Not Required)

**Requirements:**
- ⚠️ AOT compatibility **preferred** but not required
- ✅ TUnit is source-generated (AOT compatible by default)
- ✅ Rocks is source-generated (AOT compatible by default)
- ✅ Bogus is AOT compatible
- ✅ **Prefer** AOT-compatible patterns even in tests

**Why not required:**
- Some testing utilities use reflection
- Test frameworks may need dynamic proxies
- Cost/benefit: test code doesn't ship to production

**However:**
- Use AOT-compatible tools when available (TUnit, Rocks)
- Avoid reflection-heavy test patterns
- Don't create bad habits in test code

**Project File Configuration:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- Test project settings -->
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>

    <!-- Optional: Enable AOT analysis even in tests -->
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
  </PropertyGroup>
</Project>
```

---

## What Triggers AOT Incompatibility

### ❌ Reflection

```csharp
// ❌ Type.GetType - Runtime type lookup
Type orderType = Type.GetType("MyApp.Domain.Order");

// ❌ Assembly.GetTypes - Enumerating types
var types = Assembly.GetExecutingAssembly().GetTypes();

// ❌ typeof(T).GetProperties - Reflection on properties
var properties = typeof(Order).GetProperties();

// ❌ Activator.CreateInstance - Dynamic instantiation
var instance = Activator.CreateInstance(orderType);

// ❌ MethodInfo.Invoke - Reflection-based method invocation
MethodInfo method = type.GetMethod("ProcessOrder");
method.Invoke(instance, new object[] { order });

// ❌ Generic methods with MakeGenericMethod
MethodInfo genericMethod = typeof(Repository<>).GetMethod("SaveAsync");
MethodInfo boundMethod = genericMethod.MakeGenericMethod(typeof(Order));
```

---

### ❌ Dynamic Code Generation

```csharp
// ❌ Expression.Compile - Runtime compilation
Expression<Func<Order, bool>> expr = o => o.TotalAmount > 100;
var compiled = expr.Compile();  // Runtime compilation!

// ❌ DynamicMethod - Emit IL at runtime
DynamicMethod method = new DynamicMethod("Add", typeof(int), new[] { typeof(int), typeof(int) });
ILGenerator il = method.GetILGenerator();
il.Emit(OpCodes.Ldarg_0);
// ...

// ❌ Runtime type creation
var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(...);
```

---

### ❌ Reflection-Based Serializers

```csharp
// ❌ System.Text.Json without source generation
var json = JsonSerializer.Serialize(order);  // Uses reflection by default!

// ❌ BinaryFormatter (also obsolete)
var formatter = new BinaryFormatter();
formatter.Serialize(stream, order);

// ❌ Newtonsoft.Json (reflection-based)
var json = JsonConvert.SerializeObject(order);
```

---

### ❌ Late Binding

```csharp
// ❌ dynamic keyword
dynamic obj = GetSomeObject();
obj.ProcessOrder();  // Late binding!

// ❌ COM Interop (uses late binding)
dynamic excel = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application"));
```

---

## AOT-Compatible Alternatives

### ✅ Source Generators

Replace reflection with source generators that analyze code at compile time.

**Whizbang Uses:**

```csharp
// ✅ ReceptorDiscoveryGenerator - Finds IReceptor<TMessage, TResponse> implementations
// Generated at compile time, zero reflection

// In your code, just implement the interface:
public class OrderReceptor : IReceptor<CreateOrder, OrderCreated> {
    public async Task<OrderCreated> ReceiveAsync(CreateOrder message) {
        // Implementation
    }
}

// Source generator discovers this and generates routing code
```

---

### ✅ Vogen for Value Objects

```csharp
// ✅ Source-generated value objects (AOT compatible)
[ValueObject<Guid>]
public readonly partial struct OrderId { }

[ValueObject<Guid>]
public readonly partial struct CustomerId { }

// Usage
var orderId = OrderId.From(Guid.CreateVersion7());
var customerId = CustomerId.From(Guid.CreateVersion7());
```

**Vogen generates:**
- Value equality
- Validation
- Serialization support
- All without reflection!

---

### ✅ System.Text.Json with Source Generation

```csharp
// Define JSON context with source generation
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Customer))]
[JsonSerializable(typeof(List<Order>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class AppJsonSerializerContext : JsonSerializerContext { }

// Usage - AOT compatible!
var json = JsonSerializer.Serialize(order, AppJsonSerializerContext.Default.Order);
var order = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.Order);
```

**No reflection needed!**

---

### ✅ Explicit Type Handling

Instead of reflection, use explicit type handling with source generators or manual code.

**❌ Reflection-based:**
```csharp
// ❌ Dynamic dispatch based on runtime type
var handler = _serviceProvider.GetService(Type.GetType($"MyApp.Handlers.{message.GetType().Name}Handler"));
```

**✅ Source-generated:**
```csharp
// ✅ Generated dispatch code (from ReceptorDiscoveryGenerator)
public ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType) {
    if (messageType == typeof(CreateOrder)) {
        var receptor = _serviceProvider.GetRequiredService<IReceptor<CreateOrder, OrderCreated>>();
        return async msg => (TResult)(object)await receptor.ReceiveAsync((CreateOrder)msg);
    }
    if (messageType == typeof(UpdateOrder)) {
        var receptor = _serviceProvider.GetRequiredService<IReceptor<UpdateOrder, OrderUpdated>>();
        return async msg => (TResult)(object)await receptor.ReceiveAsync((UpdateOrder)msg);
    }
    return null;
}
```

All types known at compile time!

---

### ✅ UUIDv7 for Identifiers

```csharp
// ✅ Use Guid.CreateVersion7() for time-ordered, database-friendly IDs
var orderId = OrderId.From(Guid.CreateVersion7());

// EF Core 10 translates this to native uuidv7() in PostgreSQL 18+
// No reflection, no special serialization needed
```

---

## Verification Process

### Build-Time Verification

```bash
# Clean and build with AOT analysis
dotnet clean
dotnet build -c Release

# Look for AOT warnings (IL2XXX, IL3XXX)
# Example warnings to watch for:
# IL2026: Using member 'X' which has 'RequiresUnreferencedCodeAttribute'
# IL2087: Target parameter 'X' in 'Y' requires dynamic access
# IL3050: Using member 'X' which has 'RequiresDynamicCodeAttribute'
```

**Treat AOT warnings as errors!**

```xml
<PropertyGroup>
  <!-- Make AOT warnings into errors -->
  <WarningsAsErrors>IL2026;IL2087;IL3050</WarningsAsErrors>
</PropertyGroup>
```

---

### Publish-Time Verification

```bash
# Publish as native AOT (library projects)
dotnet publish -c Release -r linux-x64

# Publish as native AOT (sample projects)
cd samples/ECommerce/ECommerce.OrderService.API
dotnet publish -c Release -r linux-x64

# Verify binary is native (not managed)
file bin/Release/net10.0/linux-x64/publish/ECommerce.OrderService.API
# Should show: "ELF 64-bit LSB executable" (not "PE32")
```

---

### Runtime Verification

```bash
# Run the native binary
./bin/Release/net10.0/linux-x64/publish/ECommerce.OrderService.API

# Verify:
# - Application starts successfully
# - No runtime reflection errors
# - All features work correctly
# - Performance is excellent (native code)
```

---

## Common Mistakes

### ❌ Mistake 1: Using reflection "just this once"

```csharp
// ❌ WRONG - "It's just one line"
var types = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.IsAssignableTo(typeof(IHandler)));

// This breaks AOT completely!
```

**✅ CORRECT:** Use source generator to discover types at compile time.

---

### ❌ Mistake 2: Using default JsonSerializer

```csharp
// ❌ WRONG - Uses reflection by default
var json = JsonSerializer.Serialize(order);
```

**✅ CORRECT:** Use source-generated JsonSerializerContext:

```csharp
var json = JsonSerializer.Serialize(order, AppJsonSerializerContext.Default.Order);
```

---

### ❌ Mistake 3: Workarounds in sample projects

```csharp
// ❌ WRONG - In a sample project
// "The library doesn't support this, so I'll hack it"
private void ManuallyTrackCausation(Order order) {
    // Workaround because library doesn't support causation tracking
}
```

**✅ CORRECT:**
1. Recognize sample needs feature
2. Stop work on sample
3. Implement feature in library (with source generator)
4. Add tests
5. Then use it in sample

---

### ❌ Mistake 4: Forgetting to configure PublishAot

```xml
<!-- ❌ WRONG - Missing PublishAot -->
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>
```

**✅ CORRECT:**

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <EnableAotAnalyzer>true</EnableAotAnalyzer>
</PropertyGroup>
```

---

### ❌ Mistake 5: Using Guid.NewGuid()

```csharp
// ❌ WRONG - Random GUID (also causes database issues)
var id = Guid.NewGuid();
```

**✅ CORRECT:**

```csharp
// ✅ UUIDv7 - Time-ordered, database-friendly, AOT compatible
var id = Guid.CreateVersion7();
```

---

### ❌ Mistake 6: Assuming test code doesn't matter

```csharp
// ❌ WRONG - "It's just test code, reflection is fine"
[Test]
public async Task SomeTest() {
    var types = Assembly.GetExecutingAssembly().GetTypes();
    // Creates bad habits, harder to maintain
}
```

**✅ BETTER:** Use AOT-compatible patterns even in tests:

```csharp
[Test]
public async Task SomeTest() {
    // Use explicit types, TUnit, Rocks, Bogus
    // All AOT-compatible
}
```

---

## Quick Reference

### ✅ AOT-Compatible Technologies

- Source generators (Roslyn)
- Vogen (value objects)
- System.Text.Json with `JsonSerializerContext`
- TUnit (testing)
- Rocks (mocking)
- Bogus (fake data)
- EF Core 10 with complex types
- `Guid.CreateVersion7()` (UUIDv7)

### ❌ Avoid for AOT

- `Type.GetType()`, `Activator.CreateInstance()`
- Reflection (`MethodInfo.Invoke`, `PropertyInfo.GetValue`)
- `Expression.Compile()`
- `DynamicMethod`, IL emit
- Default `JsonSerializer` (without source generation)
- `BinaryFormatter`, Newtonsoft.Json
- `dynamic` keyword
- `MakeGenericType()`, `MakeGenericMethod()`

### Build Commands

```bash
# Clean build with AOT analysis
dotnet clean && dotnet build -c Release

# Publish as native AOT
dotnet publish -c Release -r linux-x64

# Run tests
dotnet test
```

### Verification Checklist

- [ ] No IL2XXX or IL3XXX warnings
- [ ] `PublishAot=true` in all library and sample projects
- [ ] Publishes successfully to native binary
- [ ] Native binary runs without errors
- [ ] No reflection in library code
- [ ] No reflection in sample code
- [ ] Source generators handle all type discovery
- [ ] JSON serialization uses `JsonSerializerContext`
- [ ] All IDs use `Guid.CreateVersion7()`

---

## See Also

- [.NET Native AOT Deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [Prepare .NET Libraries for Trimming](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)
- [Source Generators](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview)
- [System.Text.Json Source Generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
