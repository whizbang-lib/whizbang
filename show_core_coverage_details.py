#!/usr/bin/env python3
import xml.etree.ElementTree as ET
import os

def find_core_coverage():
    """Find Core coverage from any test project."""
    tests_dir = 'tests'
    
    for test_project in os.listdir(tests_dir):
        coverage_path = os.path.join(tests_dir, test_project, 'bin/Debug/net10.0/TestResults/coverage.cobertura.xml')
        if os.path.exists(coverage_path):
            tree = ET.parse(coverage_path)
            root = tree.getroot()
            
            for package in root.findall('.//package[@name="Whizbang.Core"]'):
                return package
    return None

def main():
    package = find_core_coverage()
    if not package:
        print("Whizbang.Core coverage not found")
        return
    
    print("=== Whizbang.Core Coverage Details ===\n")
    print("Classes with < 100% Coverage (excluding compiler-generated):\n")
    
    classes = []
    for class_elem in package.findall('.//class'):
        class_name = class_elem.get('name').split('.')[-1]
        line_rate = float(class_elem.get('line-rate', 0)) * 100
        branch_rate = float(class_elem.get('branch-rate', 0)) * 100
        
        # Skip if perfect coverage
        if line_rate == 100 and branch_rate == 100:
            continue
        
        # Skip obvious compiler-generated classes
        if class_name.startswith('<') or class_name.startswith('__'):
            continue
        
        # Skip if starts with <>c (compiler-generated)
        if class_name.startswith('<>c'):
            continue
        
        classes.append((class_name, line_rate, branch_rate))
    
    # Sort by coverage (worst first)
    classes.sort(key=lambda x: (x[1], x[2]))
    
    for class_name, line_rate, branch_rate in classes:
        status = "📝" if line_rate == 0 and branch_rate == 0 else "⚠️"
        print(f"{status} {class_name:50s} Line: {line_rate:5.1f}% | Branch: {branch_rate:5.1f}%")
    
    print(f"\nTotal classes needing coverage: {len(classes)}")

if __name__ == "__main__":
    main()
