# Source Generator Templates

This directory contains template files that are used to generate code. These templates are **real C# code files** that benefit from full IDE support.

## Why Templates?

Instead of building code strings with `StringBuilder`, we maintain templates as actual `.cs` files. This provides:

✅ **Full C# Analyzer Support** - Roslyn analyzers work on template code
✅ **IntelliSense** - Autocomplete, navigation, refactoring
✅ **Syntax Validation** - Errors caught during development
✅ **Easier Maintenance** - See the structure of generated code clearly
✅ **Better Testing** - Can validate template structure independently

## How It Works

### 1. Template Structure

Templates are regular C# files with special markers for dynamic content:

```csharp
// DispatcherTemplate.cs
public class Dispatcher : IDispatcher {
    public async Task<TResult> Send<TResult>(object message) {
        #region SEND_ROUTING
        // This region will be replaced with generated routing code
        #endregion
        throw new HandlerNotFoundException(messageType);
    }
}
```

### 2. Marker Syntax

- `{{VARIABLE}}` - Simple string replacement (e.g., `{{RECEPTOR_COUNT}}`)
- `#region NAME ... #endregion` - Region-based markers for code block injection

### 3. Configuration

Templates are configured in `Whizbang.Generators.csproj`:

```xml
<ItemGroup>
  <!-- Exclude from compilation (they reference types not in generator project) -->
  <Compile Remove="Templates\**\*.cs" />

  <!-- Include as content so IDE can analyze them -->
  <None Include="Templates\**\*.cs" />

  <!-- Embed as resources so generator can read them at runtime -->
  <EmbeddedResource Include="Templates\**\*.cs" />
</ItemGroup>
```

### 4. Usage in Generator

```csharp
private static string GenerateDispatcherSource(ImmutableArray<ReceptorInfo> receptors) {
    // Read template from embedded resource
    var template = GetEmbeddedTemplate("DispatcherTemplate.cs");

    // Generate dynamic code sections
    var routing = GenerateRoutingCode(receptors);

    // Replace markers
    var result = template
        .Replace("{{VARIABLE}}", value);

    // Replace region markers using regex (robust against whitespace)
    result = ReplaceRegion(result, "SEND_ROUTING", routing);

    return result;
}
```

## IDE Support

Since templates are excluded from compilation but included as `<None>`, they:

- ✅ Appear in Solution Explorer
- ✅ Get syntax highlighting
- ✅ Benefit from IntelliSense (with some limitations)
- ✅ Work with code analyzers
- ❌ Don't cause compilation errors (types may not be available in generator project)

## Best Practices

### DO:
- Keep template structure close to what will be generated
- Use clear, descriptive marker names
- Add comments explaining template sections
- Test templates by comparing generated output

### DON'T:
- Put complex logic in templates (do it in the generator)
- Use markers that might appear naturally in code
- Make templates too generic (optimize for readability)

## Example: Adding a New Template

1. **Create template file**: `Templates/MyTemplate.cs`
2. **Write real C# code** with markers where dynamic content goes
3. **Test in IDE** - Verify analyzer support works
4. **Update generator** to read and use the template
5. **Verify output** matches expected structure

## Troubleshooting

### Template not found at runtime
- Verify `<EmbeddedResource Include="Templates\**\*.cs" />` in csproj
- Check resource name: `Whizbang.Generators.Templates.{filename}`

### Compilation errors from template
- Ensure `<Compile Remove="Templates\**\*.cs" />` is present
- Templates reference types that don't exist in generator project - this is expected

### No analyzer support
- Check that `<None Include="Templates\**\*.cs" />` is present
- Verify template file is included in project
- Try reloading the project in your IDE

## References

This approach is based on the **AdditionalFiles pattern** used by:
- [StronglyTypedId](https://github.com/andrewlock/StronglyTypedId) - Uses `.typedid` template files
- [Vogen](https://github.com/SteveDunn/Vogen) - Similar template approach
- Microsoft's source generator documentation

## Related Files

- `ReceptorDiscoveryGenerator.cs` - Reads and processes templates
- `Whizbang.Generators.csproj` - Template configuration
- Generated code: `tests/Whizbang.Core.Tests/.whizbang-generated/`
