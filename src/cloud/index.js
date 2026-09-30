/** App-facing cloud facade — same contract as Cab Lab (the-cab-lab/cloud/index.js).
 *  OmniCam is local-first: the SQLite project / exported files are the source
 *  of truth and cloud sync only mirrors completed writes. Every helper catches
 *  provider errors into { ok:false, error } — nothing here may break a local
 *  save. Node-side module (uses node:fs); not bundled into the browser app.
 */
import fs from "node:fs";
import path from "node:path";
import { loadCloudConfig } from "./config.js";
import { cloudKey } from "./paths.js";
import { createStorageProvider } from "./providers.js";

let _config = null;
let _provider = null;
let _providerError = null;
let _lastSync = null; // { ok, key, ms, error?, at } — surfaced by status()

export function config() {
  if (!_config) _config = loadCloudConfig();
  return _config;
}

export function provider() {
  if (_provider || _providerError) return _provider;
  try {
    _provider = createStorageProvider(config());
  } catch (err) {
    _providerError = err.message;
  }
  return _provider;
}

export function status() {
  const cfg = config();
  return {
    enabled: cfg.enabled,
    provider: cfg.provider,
    bucket: cfg.gcp.bucket,
    projectId: cfg.gcp.projectId,
    region: cfg.gcp.region,
    root: cfg.root,
    ready: !!provider(),
    error: _providerError,
    lastSync: _lastSync,
  };
}

/** Record + report a sync attempt. Never throws; null = cloud disabled. */
async function safeSync(fn) {
  const t0 = Date.now();
  const p = provider();
  if (!p) return null;
  try {
    const r = await fn(p);
    _lastSync = { ok: true, key: r && r.key, ms: Date.now() - t0, at: new Date().toISOString() };
    return { ..._lastSync, ...r };
  } catch (err) {
    _lastSync = { ok: false, error: err.message, ms: Date.now() - t0, at: new Date().toISOString() };
    return _lastSync;
  }
}

/** Mirror a local file into the bucket. `rel` is under the app root. */
export function uploadLocalFile(root, rel, localPath, opts) {
  return safeSync((p) => p.upload(cloudKey(root, rel), fs.readFileSync(localPath), opts));
}

export function uploadText(root, rel, text, opts) {
  return safeSync((p) => p.upload(cloudKey(root, rel), String(text), opts));
}

export function downloadText(root, rel) {
  return safeSync(async (p) => ({ key: cloudKey(root, rel), text: (await p.download(cloudKey(root, rel))).toString("utf8") }));
}

export function downloadTo(root, rel, localPath) {
  return safeSync(async (p) => {
    const data = await p.download(cloudKey(root, rel));
    const tmp = `${localPath}.tmp-${process.pid}`;
    fs.mkdirSync(path.dirname(localPath), { recursive: true });
    fs.writeFileSync(tmp, data);
    fs.renameSync(tmp, localPath);
    return { key: cloudKey(root, rel), size: data.length };
  });
}

export function deleteKey(root, rel) {
  return safeSync((p) => p.delete(cloudKey(root, rel)).then(() => ({ key: cloudKey(root, rel) })));
}

export function listPrefix(root, rel = "") {
  return safeSync(async (p) => ({ items: await p.list(cloudKey(root, rel)) }));
}

/** Test seam: reload config/provider (checks set env vars first). */
export function _reset() {
  _config = null;
  _provider = null;
  _providerError = null;
  _lastSync = null;
}
