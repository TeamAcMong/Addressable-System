/* Compile gate for com.game.addressables.
 *
 * Compiler:  Unity 6000.5.7f1's bundled Roslyn (the only one installed).
 * Refs:      Unity 2022.3.62f3's managed assemblies — the floor package.json declares.
 *            (6000.5 refs are unusable: GetInstanceID became obsolete-as-error there and
 *             Addressables 2.3.1 source no longer compiles against it. CS0619 is an error,
 *             not suppressible by -nowarn.)
 * Deps:      every referenced asmdef is resolved recursively out of Library/PackageCache
 *            and compiled from source. Library/ScriptAssemblies is deliberately ignored —
 *            it was built against 6000.5 and mixing ABIs produces phantom errors.
 *
 * usage: node gate.js [--clean] [target ...]
 *        default targets: the package's Runtime + Editor asmdefs
 */
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

// Paths are overridable so this runs on another machine / in CI.
//   ADDR_GATE_ROOT       project root (default: two levels up from this file)
//   ADDR_GATE_CSC_UNITY  Unity install whose bundled Roslyn is used as the compiler
//   ADDR_GATE_REF_UNITY  Unity install whose managed assemblies are used as references
const ROOT = process.env.ADDR_GATE_ROOT || path.resolve(__dirname, '..', '..');
const CSC_UNITY = process.env.ADDR_GATE_CSC_UNITY ||
  'C:\\Program Files\\Unity\\Hub\\Editor\\6000.5.7f1\\Editor\\Data';
const REF_UNITY = process.env.ADDR_GATE_REF_UNITY ||
  'C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.62f3\\Editor\\Data';
const DOTNET = path.join(CSC_UNITY, 'DotNetSdk', 'dotnet.exe');

function findCsc() {
  const base = path.join(CSC_UNITY, 'DotNetSdk', 'sdk');
  try {
    for (const v of fs.readdirSync(base)) {
      const p = path.join(base, v, 'Roslyn', 'bincore', 'csc.dll');
      if (fs.existsSync(p)) return p;
    }
  } catch { }
  return path.join(base, '8.0.318', 'Roslyn', 'bincore', 'csc.dll');
}
const CSC = findCsc();
const OUT = process.env.ADDR_GATE_OUT || path.join(__dirname, '.deps');

const argv = process.argv.slice(2);
const CLEAN = argv.includes('--clean');
const PREFER_PREBUILT = argv.includes('--prebuilt-deps') || process.env.ADDR_GATE_PREBUILT_DEPS === '1';
const targets = argv.filter(a => !a.startsWith('--'));
const TARGET_NAMES = new Set(targets.length ? targets : ['AddressableManager', 'AddressableManager.Editor']);

if (CLEAN && fs.existsSync(OUT)) fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

// ---------------------------------------------------------------- defines
// Version defines MUST match the Unity whose assemblies we reference. Referencing 6000.5 while
// claiming UNITY_2022_3 compiles the wrong side of every version #if in the package.
function refUnityVersion() {
  const m = REF_UNITY.replace(/[\\/]/g, '/').match(/Editor\/(\d+)\.(\d+)\.(\d+)/);
  return m ? { major: +m[1], minor: +m[2], patch: +m[3] } : { major: 2022, minor: 3, patch: 0 };
}

function unityDefines() {
  const v = refUnityVersion();
  const d = [
    `UNITY_${v.major}_${v.minor}_${v.patch}`,
    `UNITY_${v.major}_${v.minor}`,
    `UNITY_${v.major}`,
  ];
  const chain = [[5, 3], [5, 4], [5, 5], [5, 6], [2017, 1], [2017, 2], [2017, 3], [2017, 4],
  [2018, 1], [2018, 2], [2018, 3], [2018, 4], [2019, 1], [2019, 2], [2019, 3], [2019, 4],
  [2020, 1], [2020, 2], [2020, 3], [2021, 1], [2021, 2], [2021, 3], [2022, 1], [2022, 2], [2022, 3],
  [2023, 1], [2023, 2], [2023, 3],
  [6000, 0], [6000, 1], [6000, 2], [6000, 3], [6000, 4], [6000, 5]];
  for (const [maj, min] of chain) {
    if (maj > v.major || (maj === v.major && min > v.minor)) continue;
    d.push('UNITY_' + maj + '_' + min + '_OR_NEWER');
  }
  d.push('UNITY_5_3_OR_NEWER');
  return d.concat([
    'UNITY_EDITOR', 'UNITY_EDITOR_64', 'UNITY_EDITOR_WIN',
    'UNITY_STANDALONE_WIN', 'UNITY_STANDALONE', 'PLATFORM_STANDALONE_WIN', 'PLATFORM_STANDALONE',
    'UNITY_64', 'PLATFORM_ARCH_64', 'ENABLE_MONO', 'ENABLE_PROFILER', 'DEBUG', 'TRACE',
    'UNITY_ASSERTIONS', 'UNITY_INCLUDE_TESTS',
    'NET_STANDARD_2_0', 'NET_STANDARD', 'NET_STANDARD_2_1', 'NETSTANDARD', 'NETSTANDARD2_1',
    'CSHARP_7_OR_LATER', 'CSHARP_7_3_OR_NEWER',
    'ENABLE_UNITYWEBREQUEST', 'ENABLE_WWW', 'ENABLE_CACHING', 'ENABLE_CLOUD_SERVICES',
    'ENABLE_TEXTURE_STREAMING', 'ENABLE_VIRTUALTEXTURING', 'ENABLE_UNITYEVENTS',
    'ENABLE_INPUT_SYSTEM', 'ENABLE_LEGACY_INPUT_MANAGER',
    'ENABLE_SPRITES', 'ENABLE_TERRAIN', 'ENABLE_TILEMAP', 'ENABLE_TIMELINE', 'ENABLE_DIRECTOR',
    'ENABLE_PHYSICS', 'ENABLE_AUDIO', 'ENABLE_VIDEO', 'ENABLE_LOCALIZATION',
    'ENABLE_MANAGED_JOBS', 'ENABLE_UNITY_COLLECTIONS_CHECKS', 'ENABLE_BURST_AOT',
    'TEXTCORE_1_0_OR_NEWER', 'TEXTCORE_FONT_ENGINE_1_5_OR_NEWER',
  ]);
}
const BASE_DEFINES = unityDefines();

// ---------------------------------------------------------------- engine refs
function engineRefs() {
  const refs = [];
  // Modules ONLY. The aggregate facades Managed/UnityEngine.dll and Managed/UnityEditor.dll
  // type-forward to these, so referencing both makes every UnityEngine type ambiguous (CS0433).
  const eng = path.join(REF_UNITY, 'Managed', 'UnityEngine');
  for (const f of fs.readdirSync(eng)) if (f.endsWith('.dll')) refs.push(path.join(eng, f));
  const ns = path.join(REF_UNITY, 'NetStandard', 'ref', '2.1.0', 'netstandard.dll');
  if (fs.existsSync(ns)) refs.push(ns);
  const compat = path.join(REF_UNITY, 'NetStandard', 'compat', '2.1.0', 'shims', 'netstandard');
  if (fs.existsSync(compat)) {
    for (const f of fs.readdirSync(compat)) if (f.endsWith('.dll')) refs.push(path.join(compat, f));
  }
  const extra = path.join(REF_UNITY, 'Managed');
  for (const f of ['Newtonsoft.Json.dll', 'Unity.Cecil.dll']) {
    const p = path.join(extra, f);
    if (fs.existsSync(p)) refs.push(p);
  }
  return refs;
}
const ENGINE = engineRefs();

// ---------------------------------------------------------------- asmdef registry
const byName = new Map(), byGuid = new Map();

function scanAsmdefs(dir, depth = 0) {
  if (depth > 12) return;
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      if (e.name.endsWith('~') || e.name === 'Tests' || e.name === '.git' ||
        e.name === 'node_modules' || e.name === 'Documentation') continue;
      scanAsmdefs(p, depth + 1);
    } else if (e.name.endsWith('.asmdef')) {
      let json;
      try { json = JSON.parse(fs.readFileSync(p, 'utf8')); } catch { continue; }
      if (!json.name) continue;
      if (/\.Tests?$/i.test(json.name) || /Tests\./i.test(json.name)) continue;
      const entry = { name: json.name, json, dir: path.dirname(p), file: p };
      if (!byName.has(json.name)) byName.set(json.name, entry);
      const meta = p + '.meta';
      if (fs.existsSync(meta)) {
        const g = fs.readFileSync(meta, 'utf8').match(/guid:\s*([0-9a-f]{32})/);
        if (g && !byGuid.has(g[1])) byGuid.set(g[1], entry);
      }
    }
  }
}
scanAsmdefs(path.join(ROOT, 'Library', 'PackageCache'));
scanAsmdefs(path.join(ROOT, 'Packages'));
scanAsmdefs(path.join(ROOT, 'Assets'));

// which packages exist -> satisfies versionDefines (expression range is approximated as "present")
const presentPackages = new Set();
try {
  for (const d of fs.readdirSync(path.join(ROOT, 'Library', 'PackageCache')))
    presentPackages.add(d.split('@')[0]);
} catch { }
try {
  for (const d of fs.readdirSync(path.join(ROOT, 'Packages'), { withFileTypes: true }))
    if (d.isDirectory()) presentPackages.add(d.name);
} catch { }

function resolveRef(r) {
  if (typeof r !== 'string') return null;
  if (r.startsWith('GUID:')) return byGuid.get(r.slice(5)) || null;
  return byName.get(r) || null;
}

function sources(dir) {
  const out = [];
  (function walk(d) {
    let es;
    try { es = fs.readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const e of es) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) {
        if (e.name.endsWith('~')) continue;
        // a nested asmdef owns its own subtree
        if (fs.readdirSync(p).some(f => f.endsWith('.asmdef'))) continue;
        walk(p);
      } else if (e.name.endsWith('.cs')) out.push(p);
    }
  })(dir);
  return out;
}

function precompiled(entry) {
  // dlls shipped inside the package folder
  const out = [];
  (function walk(d, depth) {
    if (depth > 4) return;
    let es; try { es = fs.readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const e of es) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) { if (!e.name.endsWith('~')) walk(p, depth + 1); }
      else if (e.name.endsWith('.dll')) out.push(p);
    }
  })(entry.dir, 0);
  return out;
}

const q = s => '"' + s + '"';
const built = new Map();   // name -> dll path | null (failed)
const building = new Set();
const failures = [];
const prebuiltFallbacks = [];

function build(entry, chain = []) {
  if (built.has(entry.name)) return built.get(entry.name);
  if (building.has(entry.name)) return null;           // cycle guard
  building.add(entry.name);

  const deps = [];
  for (const r of entry.json.references || []) {
    const dep = resolveRef(r);
    if (!dep) continue;
    const d = build(dep, chain.concat(entry.name));
    if (d) deps.push(d);
  }

  // Unity implicitly references every autoReferenced assembly. Reproducing that fully would
  // mean building all 78 discovered asmdefs, so approximate it for OUR assemblies only, with
  // the ones this package actually touches. (UnityEngine.UI is used by Runtime/UI/* without a
  // matching entry in the asmdef's `references` — it compiles in Unity purely via autoReference.)
  if (entry.dir.includes('com.game.addressables') && !entry.json.overrideReferences) {
    for (const n of ['UnityEngine.UI', 'Unity.TextMeshPro', 'UnityEditor.UI']) {
      const e = byName.get(n);
      if (!e || e.name === entry.name) continue;
      const d = build(e, chain.concat(entry.name));
      if (d && !deps.includes(d)) deps.push(d);
    }
  }

  const outDll = path.join(OUT, entry.name + '.dll');
  const srcs = sources(entry.dir);

  // When the project's own Library/ScriptAssemblies was produced by the SAME Unity version the
  // gate references, those assemblies are exact and building dependencies from source only adds
  // risk. Targets are always compiled from source — it is their code we are checking.
  if (PREFER_PREBUILT && !TARGET_NAMES.has(entry.name)) {
    const prebuilt = path.join(ROOT, 'Library', 'ScriptAssemblies', entry.name + '.dll');
    if (fs.existsSync(prebuilt)) {
      building.delete(entry.name);
      built.set(entry.name, prebuilt);
      return prebuilt;
    }
  }

  if (fs.existsSync(outDll) && !CLEAN) {
    const newest = srcs.reduce((a, s) => Math.max(a, fs.statSync(s).mtimeMs), 0);
    if (fs.statSync(outDll).mtimeMs >= newest) {
      building.delete(entry.name);
      built.set(entry.name, outDll);
      return outDll;
    }
  }

  if (!srcs.length) { building.delete(entry.name); built.set(entry.name, null); return null; }

  const defines = BASE_DEFINES.slice();
  for (const vd of entry.json.versionDefines || []) {
    if (vd.name === 'Unity' || presentPackages.has(vd.name)) defines.push(vd.define);
  }

  const rspPath = path.join(OUT, entry.name + '.rsp');
  fs.writeFileSync(rspPath, [
    '-target:library', '-nostdlib+', '-noconfig', '-nologo', '-langversion:9.0',
    entry.json.allowUnsafeCode ? '-unsafe+' : '-unsafe-',
    '-nowarn:0169,0649,0414,0618,0067,0108,0114,1701,1702,0162,0219,0429,0184,0472,4014,0067,0436',
    '-out:' + q(outDll),
    '-define:' + defines.join(';'),
    ...ENGINE.concat(deps).concat(precompiled(entry)).map(r => '-r:' + q(r)),
    ...srcs.map(q),
  ].join('\n'), 'utf8');

  let stdout = '';
  try {
    stdout = execFileSync(DOTNET, [CSC, '@' + rspPath], { encoding: 'utf8', maxBuffer: 128 * 1024 * 1024 });
  } catch (e) { stdout = (e.stdout || '') + (e.stderr || ''); }

  const errs = stdout.split(/\r?\n/).filter(l => /error [A-Z]{2}\d+/.test(l));
  building.delete(entry.name);

  if (errs.length) {
    // Some Unity assemblies (internal-API bridges) are compiled by the Editor with
    // privileged flags we cannot reproduce. If Unity already produced the assembly,
    // use that instead of failing the whole graph — it is a dependency, not our code.
    //
    // NEVER for a target: silently substituting a previously-built dll for the code under test
    // turns a compile failure into a green run, which is worse than having no gate at all.
    const prebuilt = TARGET_NAMES.has(entry.name)
      ? null
      : path.join(ROOT, 'Library', 'ScriptAssemblies', entry.name + '.dll');
    if (prebuilt && fs.existsSync(prebuilt)) {
      prebuiltFallbacks.push(entry.name);
      built.set(entry.name, prebuilt);
      return prebuilt;
    }
    failures.push({ name: entry.name, errors: errs, srcCount: srcs.length });
    built.set(entry.name, null);
    return null;
  }
  built.set(entry.name, outDll);
  return outDll;
}

// ---------------------------------------------------------------- run
const DEFAULT_TARGETS = ['AddressableManager', 'AddressableManager.Editor'];
const wanted = targets.length ? targets : DEFAULT_TARGETS;

const results = [];
for (const name of wanted) {
  const entry = byName.get(name);
  if (!entry) { console.log('?? target asmdef not found: ' + name); results.push([name, 'NOT FOUND']); continue; }
  const dll = build(entry);
  results.push([name, dll ? 'OK' : 'FAILED']);
}

console.log('==================== COMPILE GATE ====================');
console.log('root:     ' + ROOT);
console.log('compiler: ' + path.basename(path.dirname(path.dirname(CSC_UNITY))) + ' Roslyn');
console.log('refs:     ' + path.basename(path.dirname(path.dirname(REF_UNITY))) +
  (PREFER_PREBUILT ? '   (deps: prebuilt from Library/ScriptAssemblies)' : ''));
console.log('asmdefs discovered: ' + byName.size + '   built: ' + [...built.values()].filter(Boolean).length);
console.log('');

const targetFailures = failures.filter(f => wanted.includes(f.name));
const depFailures = failures.filter(f => !wanted.includes(f.name));

if (prebuiltFallbacks.length) {
  console.log('--- used Unity-prebuilt dll (source build needs privileged flags) ---');
  console.log('  ' + prebuiltFallbacks.join(', '));
  console.log('');
}

if (depFailures.length) {
  console.log('--- dependency assemblies that failed (not our code) ---');
  for (const f of depFailures) console.log('  ' + f.name + ': ' + f.errors.length + ' errors');
  console.log('');
}

for (const f of targetFailures) {
  console.log('--- ' + f.name + ': ' + f.errors.length + ' ERRORS (' + f.srcCount + ' sources) ---');
  f.errors.slice(0, 50).forEach(e => console.log('  ' + e.replace(ROOT + '\\', '')));
  if (f.errors.length > 50) console.log('  ... +' + (f.errors.length - 50) + ' more');
  console.log('');
}

for (const [n, s] of results) console.log((s === 'OK' ? '  PASS  ' : '  FAIL  ') + n + '  ' + s);
process.exit(results.every(r => r[1] === 'OK') ? 0 : 1);
