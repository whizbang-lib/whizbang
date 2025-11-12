#!/bin/bash

echo "=== Merging All Coverage Files ==="

# Find all coverage files
coverage_files=$(find tests -path "*/bin/Debug/net10.0/TestResults/coverage.cobertura.xml" -type f)

if [ -z "$coverage_files" ]; then
  echo "No coverage files found!"
  exit 1
fi

# Create a list file for dotnet-coverage merge
rm -f coverage_files.txt
for file in $coverage_files; do
  echo "$file" >> coverage_files.txt
done

echo "Found $(wc -l < coverage_files.txt) coverage files"

# Merge using dotnet-coverage
dotnet-coverage merge -f cobertura -o merged_coverage.cobertura.xml @coverage_files.txt

echo "Merged coverage written to: merged_coverage.cobertura.xml"
