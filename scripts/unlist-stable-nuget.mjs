#!/usr/bin/env node
// Unlists the STABLE (non-prerelease) versions of every SoftwareExtravaganza.
// Whizbang.* package on nuget.org, so the project presents as prerelease-only
// (no accidental "latest stable" like the stray 0.9.4). Unlisting hides a
// version from search/latest resolution; it stays installable by exact version
// (nuget never hard-deletes), so existing pinned consumers are unaffected.
//
// Package list is hardcoded (derived from the library's packable projects) so
// this does NOT depend on nuget's search index — which goes empty/stale for a
// while right after a mass unlist. Only touches versions that are CURRENTLY
// LISTED (via the registration endpoint), so re-runs skip finished work, and
// paces the deletes so nuget's rate limiter doesn't drop the batch tail.
//
// Dry-run by default. To apply:
//   NUGET_API_KEY=<your key> node scripts/unlist-stable-nuget.mjs --apply
// Scope, e.g. only 0.9.x:  ... --apply --version-prefix 0.9
//
// The API key needs "Unlist package" scope. `dotnet` must be on PATH.

import { execFileSync } from 'child_process';
import { gunzipSync } from 'zlib';

const CANDIDATES = [
  'SoftwareExtravaganza.Whizbang.CLI',
  'SoftwareExtravaganza.Whizbang.Core',
  'SoftwareExtravaganza.Whizbang.Data.Dapper.Custom',
  'SoftwareExtravaganza.Whizbang.Data.Dapper.Postgres',
  'SoftwareExtravaganza.Whizbang.Data.Dapper.Sqlite',
  'SoftwareExtravaganza.Whizbang.Data.EFCore.Custom',
  'SoftwareExtravaganza.Whizbang.Data.EFCore.Postgres',
  'SoftwareExtravaganza.Whizbang.Data.EFCore.Postgres.Generators',
  'SoftwareExtravaganza.Whizbang.Data.Postgres',
  'SoftwareExtravaganza.Whizbang.Data.Schema',
  'SoftwareExtravaganza.Whizbang.Generators',
  'SoftwareExtravaganza.Whizbang.Hosting.AspNet',
  'SoftwareExtravaganza.Whizbang.Hosting.Azure.ServiceBus',
  'SoftwareExtravaganza.Whizbang.Hosting.RabbitMQ',
  'SoftwareExtravaganza.Whizbang.LanguageServer',
  'SoftwareExtravaganza.Whizbang.Migrate',
  'SoftwareExtravaganza.Whizbang.Observability',
  'SoftwareExtravaganza.Whizbang.Offloads.AzureBlob',
  'SoftwareExtravaganza.Whizbang.Offloads.InMemory',
  'SoftwareExtravaganza.Whizbang.Sagas',
  'SoftwareExtravaganza.Whizbang.Sagas.Contracts',
  'SoftwareExtravaganza.Whizbang.Sagas.Generators',
  'SoftwareExtravaganza.Whizbang.SignalR',
  'SoftwareExtravaganza.Whizbang.Transports.AzureServiceBus',
  'SoftwareExtravaganza.Whizbang.Transports.FastEndpoints',
  'SoftwareExtravaganza.Whizbang.Transports.FastEndpoints.Generators',
  'SoftwareExtravaganza.Whizbang.Transports.HotChocolate',
  'SoftwareExtravaganza.Whizbang.Transports.HotChocolate.Generators',
  'SoftwareExtravaganza.Whizbang.Transports.Mutations',
  'SoftwareExtravaganza.Whizbang.Transports.RabbitMQ',
];

const APPLY = process.argv.includes('--apply');
const prefixArg = process.argv.indexOf('--version-prefix');
const VERSION_PREFIX = prefixArg !== -1 ? process.argv[prefixArg + 1] : '';
const SOURCE = 'https://api.nuget.org/v3/index.json';
const KEY = process.env.NUGET_API_KEY;
const DELAY_MS = 1200; // pace deletes to avoid nuget throttling

if (APPLY && !KEY) {
  console.error('NUGET_API_KEY env var required with --apply.');
  process.exit(1);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function getJson(url) {
  const r = await fetch(url);
  if (r.status === 404) return null; // package not published
  if (!r.ok) throw new Error(`${r.status} ${url}`);
  const buf = Buffer.from(await r.arrayBuffer());
  return JSON.parse(buf[0] === 0x1f && buf[1] === 0x8b ? gunzipSync(buf) : buf);
}

// Currently-LISTED stable versions, from the registration endpoint (authoritative
// for listed status; unaffected by search-index churn).
async function listedStables(id) {
  const d = await getJson(`https://api.nuget.org/v3/registration5-semver1/${id.toLowerCase()}/index.json`);
  if (!d) return [];
  const out = [];
  for (const page of d.items || []) {
    const items = page.items || (await getJson(page['@id']))?.items || [];
    for (const it of items) {
      const ce = it.catalogEntry;
      if (!ce.version.includes('-') && ce.version.startsWith(VERSION_PREFIX) && ce.listed !== false) {
        out.push(ce.version);
      }
    }
  }
  return out;
}

let planned = 0, done = 0, failed = 0, pkgsTouched = 0;
for (const id of CANDIDATES) {
  const stables = await listedStables(id);
  if (stables.length === 0) continue;
  pkgsTouched++;
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
    await sleep(DELAY_MS);
  }
}

console.log(
  APPLY
    ? `\nDone: ${done} unlisted, ${failed} failed of ${planned} still-listed versions across ${pkgsTouched} packages.` +
        (failed ? ' Re-run to retry the failures (finished ones are skipped).' : '')
    : `\n[dry-run] ${planned} still-listed stable versions across ${pkgsTouched} packages. Re-run with NUGET_API_KEY=… --apply.`
);
