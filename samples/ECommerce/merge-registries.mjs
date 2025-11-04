#!/usr/bin/env node

/**
 * Merge multiple Whizbang message registry files into a single master registry.
 *
 * This script:
 * 1. Finds all .whizbang/message-registry.json files in the solution
 * 2. Merges messages by type name
 * 3. Combines dispatchers, receptors, and perspectives (avoiding duplicates)
 * 4. Writes the master registry to the solution root
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Configuration
const SOLUTION_DIR = __dirname;
const OUTPUT_FILE = path.join(SOLUTION_DIR, '.whizbang', 'message-registry.json');
const REGISTRY_FILE_NAME = 'message-registry.json';

/**
 * Recursively find all message-registry.json files
 */
function findRegistryFiles(dir, files = []) {
  const entries = fs.readdirSync(dir, { withFileTypes: true });

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);

    // Skip common directories we don't need to search
    if (entry.isDirectory()) {
      if (entry.name === 'node_modules' ||
          entry.name === 'bin' ||
          entry.name === 'obj' ||
          entry.name === '.git' ||
          entry.name === '.vs') {
        continue;
      }

      // Recurse into subdirectories
      findRegistryFiles(fullPath, files);
    } else if (entry.isFile() && entry.name === REGISTRY_FILE_NAME) {
      files.push(fullPath);
    }
  }

  return files;
}

/**
 * Load and parse a registry file
 */
function loadRegistry(filePath) {
  try {
    const content = fs.readFileSync(filePath, 'utf-8');
    return JSON.parse(content);
  } catch (error) {
    console.error(`Failed to load registry from ${filePath}: ${error.message}`);
    return { messages: [] };
  }
}

/**
 * Merge two location arrays, avoiding duplicates
 */
function mergeLocations(existing, incoming) {
  const merged = [...existing];

  for (const loc of incoming) {
    // Check if this location is already in the list
    const isDuplicate = merged.some(
      m => m.filePath === loc.filePath && m.lineNumber === loc.lineNumber
    );

    if (!isDuplicate) {
      merged.push(loc);
    }
  }

  return merged;
}

/**
 * Merge multiple registries into one
 */
function mergeRegistries(registries) {
  const messageMap = new Map();

  for (const registry of registries) {
    for (const message of registry.messages) {
      const existing = messageMap.get(message.type);

      if (existing) {
        // Merge dispatchers, receptors, and perspectives
        existing.dispatchers = mergeLocations(existing.dispatchers, message.dispatchers);
        existing.receptors = mergeLocations(existing.receptors, message.receptors);
        existing.perspectives = mergeLocations(existing.perspectives, message.perspectives);
      } else {
        // First time seeing this message type - clone it
        messageMap.set(message.type, {
          type: message.type,
          isCommand: message.isCommand,
          isEvent: message.isEvent,
          filePath: message.filePath,
          lineNumber: message.lineNumber,
          dispatchers: [...message.dispatchers],
          receptors: [...message.receptors],
          perspectives: [...message.perspectives]
        });
      }
    }
  }

  // Convert map to array
  const messages = Array.from(messageMap.values());

  return { messages };
}

/**
 * Main execution
 */
function main() {
  console.log('=== Whizbang Registry Merger ===');
  console.log(`Solution directory: ${SOLUTION_DIR}`);

  // Find all registry files
  const registryFiles = findRegistryFiles(SOLUTION_DIR);

  // Exclude the output file if it exists
  const sourceFiles = registryFiles.filter(f => f !== OUTPUT_FILE);

  console.log(`Found ${sourceFiles.length} registry file(s):`);
  for (const file of sourceFiles) {
    const relativePath = path.relative(SOLUTION_DIR, file);
    console.log(`  - ${relativePath}`);
  }

  if (sourceFiles.length === 0) {
    console.log('No registry files found. Skipping merge.');
    return;
  }

  // Load all registries
  const registries = sourceFiles.map(loadRegistry);

  // Merge them
  const masterRegistry = mergeRegistries(registries);

  // Statistics
  const totalDispatchers = masterRegistry.messages.reduce((sum, m) => sum + m.dispatchers.length, 0);
  const totalReceptors = masterRegistry.messages.reduce((sum, m) => sum + m.receptors.length, 0);
  const totalPerspectives = masterRegistry.messages.reduce((sum, m) => sum + m.perspectives.length, 0);

  console.log('\nMerge results:');
  console.log(`  Messages: ${masterRegistry.messages.length}`);
  console.log(`  Dispatchers: ${totalDispatchers}`);
  console.log(`  Receptors: ${totalReceptors}`);
  console.log(`  Perspectives: ${totalPerspectives}`);

  // Ensure output directory exists
  const outputDir = path.dirname(OUTPUT_FILE);
  if (!fs.existsSync(outputDir)) {
    fs.mkdirSync(outputDir, { recursive: true });
  }

  // Write the master registry
  fs.writeFileSync(OUTPUT_FILE, JSON.stringify(masterRegistry, null, 2), 'utf-8');

  const relativeOutput = path.relative(SOLUTION_DIR, OUTPUT_FILE);
  console.log(`\nMaster registry written to: ${relativeOutput}`);
  console.log('=== Merge Complete ===');
}

main();
