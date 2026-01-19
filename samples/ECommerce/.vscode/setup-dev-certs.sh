#!/bin/bash
# Setup .NET development certificates for HTTPS

echo "Checking .NET development certificates..."

# Check if certificate exists and is trusted
if dotnet dev-certs https --check --trust &>/dev/null; then
    echo "✓ Development certificates are already configured"
    exit 0
fi

echo "Development certificates need to be configured"
echo "Cleaning old certificates..."
dotnet dev-certs https --clean

echo "Creating new development certificate..."
dotnet dev-certs https --trust

if [ $? -eq 0 ]; then
    echo "✓ Development certificates configured successfully"
    exit 0
else
    echo "⚠ Warning: Could not configure certificates automatically"
    echo "You may need to run: dotnet dev-certs https --trust"
    exit 0  # Don't fail the build
fi
