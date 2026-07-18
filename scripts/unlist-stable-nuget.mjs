#!/usr/bin/env node
// Unlists the STABLE (non-prerelease) versions of every SoftwareExtravaganza.
// Whizbang.* package on nuget.org, so the project presents as prerelease-only
// (no accidental "latest stable" like the stray 0.9.4). Unlisting hides a
// version from search/latest resolution; it stays installable by exact version
// (nuget never hard-deletes), so existing pinned consumers are unaffected.
//
// Dry-run by default (prints what it would unlist). To apply:
//   NUGET_API_KEY=<your key> node scripts/unlist-stable-nuget.mjs --apply
//
// Scope it with --version-prefix to limit which stables are touched, e.g.
// only the 0.9.x line:  node scripts/unlist-stable-nuget.mjs --apply --version-prefix 0.9
//
// The API key needs "Unlist package" scope for the SoftwareExtravaganza.*
// packages. `dotnet` must be on PATH.

import { execFileSync } from 'child_process';

const APPLY = process.argv.includes('--apply');
const prefixArg = process.argv.indexOf('--version-prefix');
const VERSION_PREFIX = prefixArg !== -1 ? process.argv[prefixArg + 1] : '';
const SOURCE = 'https://api.nuget.org/v3/index.json';
const KEY = process.env.NUGET_API_KEY;

if (APPLY && !KEY) {
  console.error('NUGET_API_KEY env var required with --apply.');
  process.exit(1);
}

async function packageIds() {
  const ids = new Set();
  for (let skip = 0; ; skip += 100) {
    const r = await fetch(`https://azuresearch-usnc.nuget.org/query?q=SoftwareExtravaganza.Whizbang&prerelease=true&take=100&skip=${skip}`);
    const { data } = await r.json();
    if (!data?.length) break;
    for (const p of data) if (p.id.startsWith('SoftwareExtravaganza.Whizbang')) ids.add(p.id);
    if (data.length < 100) break;
  }
  return [...ids].sort();
}

async function stableVersions(id) {
  const r = await fetch(`https://api.nuget.org/v3-flatcontainer/${id.toLowerCase()}/index.json`);
  if (!r.ok) return [];
  const { versions } = await r.json();
  return versions.filter((v) => !v.includes('-') && v.startsWith(VERSION_PREFIX));
}

const ids = await packageIds();
console.log(`${ids.length} SoftwareExtravaganza.Whizbang.* packages found.\n`);

let planned = 0, done = 0, failed = 0;
for (const id of ids) {
  const stables = await stableVersions(id);
  if (stables.length === 0) continue;
  for (const v of stables) {
    planned++;
    if (!APPLY) {
      console.log(`  would unlist  ${id} ${v}`);
      continue;
    }
    try {
      execFileSync('dotnet', ['nuget', 'delete', id, v, '--api-key', KEY, '--source', SOURCE, '--non-interactive'], { stdio: 'pipe' });
      console.log(`  ✓ unlisted    ${id} ${v}`);
      done++;
    } catch (e) {
      console.error(`  ✗ FAILED      ${id} ${v} — ${(e.stderr || e.message).toString().trim().split('\n').pop()}`);
      failed++;
    }
  }
}

console.log(
  APPLY
    ? `\nDone: ${done} unlisted, ${failed} failed of ${planned} stable versions.`
    : `\n[dry-run] ${planned} stable versions would be unlisted. Re-run with NUGET_API_KEY=… --apply.`
);
