#!/usr/bin/env dotnet-script
#r "nuget: System.Text.Json, 9.0.0"

using System;
using System.Text.Json;

// Minimal test to verify WhizbangId serialization fix works
// This simulates what happens in the full integration test

// 1. Force assembly loading
Console.WriteLine("Loading ECommerce.Contracts assembly...");
var contractsAsm = typeof(ECommerce.Contracts.Commands.CreateOrderCommand).Assembly;
Console.WriteLine($"Loaded: {contractsAsm.FullName}");

// 2. Check if WhizbangIdConverterInitializer exists
var initType = contractsAsm.GetType("ECommerce.Contracts.Generated.WhizbangIdConverterInitializer");
if (initType == null) {
    Console.WriteLine("ERROR: WhizbangIdConverterInitializer not found in generated code!");
    return 1;
}

Console.WriteLine($"Found: {initType.FullName}");

// 3. Call Initialize explicitly (simulate test fixture)
var initMethod = initType.GetMethod("Initialize");
if (initMethod == null) {
    Console.WriteLine("ERROR: Initialize method not found!");
    return 1;
}

Console.WriteLine("Calling WhizbangIdConverterInitializer.Initialize()...");
initMethod.Invoke(null, null);

// 4. Create JSON options and test serialization
Console.WriteLine("Creating JSON options...");
var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

// 5. Create a WhizbangId and serialize it
Console.WriteLine("Creating OrderId...");
var orderId = ECommerce.Contracts.Commands.OrderId.New();
Console.WriteLine($"OrderId: {orderId.Value}");

Console.WriteLine("Serializing OrderId...");
var json = JsonSerializer.Serialize(orderId, jsonOptions);
Console.WriteLine($"JSON: {json}");

Console.WriteLine("Deserializing OrderId...");
var deserialized = JsonSerializer.Deserialize<ECommerce.Contracts.Commands.OrderId>(json, jsonOptions);
Console.WriteLine($"Deserialized: {deserialized.Value}");

// 6. Verify round-trip
if (orderId.Value == deserialized.Value) {
    Console.WriteLine("SUCCESS: WhizbangId serialization works!");
    return 0;
} else {
    Console.WriteLine("ERROR: OrderId values don't match after round-trip!");
    return 1;
}
