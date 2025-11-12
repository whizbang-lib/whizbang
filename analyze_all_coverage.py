#!/usr/bin/env python3
import xml.etree.ElementTree as ET
import os
from collections import defaultdict

def find_latest_coverage_files():
    """Find the most recent coverage.cobertura.xml file in each test project."""
    coverage_files = []
    tests_dir = 'tests'
    
    for test_project in os.listdir(tests_dir):
        coverage_path = os.path.join(tests_dir, test_project, 'bin/Debug/net10.0/TestResults/coverage.cobertura.xml')
        if os.path.exists(coverage_path):
            coverage_files.append((test_project, coverage_path))
    
    return coverage_files

def parse_coverage(file_path):
    """Parse a coverage file and return package-level metrics."""
    if not os.path.exists(file_path):
        return {}
    
    tree = ET.parse(file_path)
    root = tree.getroot()
    
    packages = {}
    for package in root.findall('.//package'):
        name = package.get('name')
        if not name.startswith('Whizbang'):
            continue
        
        # Skip test packages in coverage analysis
        if '.Tests' in name:
            continue
            
        line_rate = float(package.get('line-rate', 0)) * 100
        branch_rate = float(package.get('branch-rate', 0)) * 100
        
        classes = {}
        for class_elem in package.findall('.//class'):
            class_name = class_elem.get('name').split('.')[-1]
            class_line = float(class_elem.get('line-rate', 0)) * 100
            class_branch = float(class_elem.get('branch-rate', 0)) * 100
            
            # Skip compiler-generated state machines with high coverage
            if class_line >= 90 and class_branch >= 90:
                continue
            
            classes[class_name] = (class_line, class_branch)
        
        if name not in packages:
            packages[name] = {
                'line': line_rate,
                'branch': branch_rate,
                'classes': classes
            }
        else:
            # Take the higher coverage numbers
            packages[name]['line'] = max(packages[name]['line'], line_rate)
            packages[name]['branch'] = max(packages[name]['branch'], branch_rate)
            packages[name]['classes'].update(classes)
    
    return packages

def main():
    coverage_files = find_latest_coverage_files()
    
    if not coverage_files:
        print("No coverage files found. Run tests with coverage first.")
        return
    
    print(f"=== Analyzing Coverage from {len(coverage_files)} Test Projects ===\n")
    
    # Aggregate all packages across all test runs
    all_packages = defaultdict(lambda: {'line': 0, 'branch': 0, 'classes': {}})
    
    for test_project, file_path in coverage_files:
        packages = parse_coverage(file_path)
        for pkg_name, metrics in packages.items():
            # Take maximum coverage across all test projects
            all_packages[pkg_name]['line'] = max(all_packages[pkg_name]['line'], metrics['line'])
            all_packages[pkg_name]['branch'] = max(all_packages[pkg_name]['branch'], metrics['branch'])
            all_packages[pkg_name]['classes'].update(metrics['classes'])
    
    # Display results
    total_packages = len(all_packages)
    packages_with_100 = 0
    
    print("Package Coverage Summary:\n")
    for pkg_name in sorted(all_packages.keys()):
        metrics = all_packages[pkg_name]
        line = metrics['line']
        branch = metrics['branch']
        
        status = ""
        if line == 100.0 and branch == 100.0:
            status = " ✓ 100%"
            packages_with_100 += 1
        elif line >= 95.0 and branch >= 95.0:
            status = " ⚠ Near"
        else:
            status = " ✗ Needs Work"
        
        print(f"{pkg_name:45s} Line: {line:5.1f}% | Branch: {branch:5.1f}%{status}")
        
        # Show classes needing coverage
        classes = metrics['classes']
        if classes:
            print(f"  Classes needing coverage:")
            for class_name, (class_line, class_branch) in sorted(classes.items())[:10]:
                if class_line < 100 or class_branch < 100:
                    print(f"    • {class_name:40s} Line: {class_line:5.1f}% | Branch: {class_branch:5.1f}%")
            if len(classes) > 10:
                print(f"    ... and {len(classes) - 10} more classes")
            print()
    
    print(f"=== Summary ===")
    print(f"Total Packages: {total_packages}")
    print(f"100% Coverage: {packages_with_100}")
    print(f"Packages Needing Work: {total_packages - packages_with_100}")

if __name__ == "__main__":
    main()
