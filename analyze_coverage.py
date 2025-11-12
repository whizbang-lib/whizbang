#!/usr/bin/env python3
import xml.etree.ElementTree as ET
import os
from collections import defaultdict

def find_coverage_files():
    """Find all coverage.cobertura.xml files in test directories."""
    coverage_files = []
    for root, dirs, files in os.walk('tests'):
        if 'coverage.cobertura.xml' in files:
            coverage_files.append(os.path.join(root, 'coverage.cobertura.xml'))
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
            
        line_rate = float(package.get('line-rate', 0)) * 100
        branch_rate = float(package.get('branch-rate', 0)) * 100
        
        classes = {}
        for class_elem in package.findall('.//class'):
            class_name = class_elem.get('name').split('.')[-1]
            class_line = float(class_elem.get('line-rate', 0)) * 100
            class_branch = float(class_elem.get('branch-rate', 0)) * 100
            classes[class_name] = (class_line, class_branch)
        
        packages[name] = {
            'line': line_rate,
            'branch': branch_rate,
            'classes': classes
        }
    
    return packages

def main():
    coverage_files = find_coverage_files()
    
    if not coverage_files:
        print("No coverage files found. Run tests with coverage first.")
        return
    
    all_packages = defaultdict(dict)
    
    for file_path in coverage_files:
        packages = parse_coverage(file_path)
        for pkg_name, metrics in packages.items():
            all_packages[pkg_name].update(metrics)
    
    print("=== Whizbang Solution Coverage Summary ===\n")
    
    total_packages = len(all_packages)
    packages_with_100 = 0
    
    for pkg_name in sorted(all_packages.keys()):
        metrics = all_packages[pkg_name]
        line = metrics.get('line', 0)
        branch = metrics.get('branch', 0)
        
        status = ""
        if line == 100.0 and branch == 100.0:
            status = " ✓ 100%"
            packages_with_100 += 1
        elif line >= 90.0 and branch >= 90.0:
            status = " ⚠ Near"
        else:
            status = " ✗ Low"
        
        print(f"{pkg_name:45s} Line: {line:5.1f}% | Branch: {branch:5.1f}%{status}")
        
        # Show classes with <100% coverage
        classes = metrics.get('classes', {})
        for class_name, (class_line, class_branch) in sorted(classes.items()):
            if class_line < 100.0 or class_branch < 100.0:
                print(f"  └─ {class_name:40s} Line: {class_line:5.1f}% | Branch: {class_branch:5.1f}%")
    
    print(f"\n=== Summary ===")
    print(f"Total Packages: {total_packages}")
    print(f"100% Coverage: {packages_with_100}")
    print(f"Coverage Rate: {packages_with_100}/{total_packages} ({packages_with_100*100//total_packages if total_packages > 0 else 0}%)")

if __name__ == "__main__":
    main()
