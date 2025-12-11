Show source generator diagnostics.

Execute:
```bash
pwsh scripts/diagnostics/show-diagnostics.ps1
```

This will display all WHIZ diagnostics from the source generator:
- **WHIZ001** (Info) - Discovered receptors (green)
- **WHIZ002** (Warning) - No receptors found (yellow)
- **WHIZ003** (Error) - Invalid receptor implementations (red)

Example output:
```
🔍 Running Whizbang source generator diagnostics...

info WHIZ001: Found receptor 'OrderReceptor' handling CreateOrder → OrderCreated
info WHIZ001: Found receptor 'PaymentReceptor' handling ProcessPayment → PaymentProcessed
```

Use when:
- Debugging generator issues
- Verifying receptor discovery
- Checking what the generator found
