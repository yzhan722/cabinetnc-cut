/** StorageProvider implementations — same surface as Cab Lab
 *  (the-cab-lab/cloud/providers.js):
 *    upload(key, data, opts?)   data: Buffer | Uint8Array | string
 *    download(key)              → Buffer
 *    exists(key)                → boolean
 *    delete(key)                → void (missing is not an error)
 *    list(prefix)               → [{ key, size, updated }]
 *  Remote failures throw CloudStorageError — callers that mirror local work
 *  must catch and log (local-first).
 */
import fs from "node:fs";
import path from "node:path";
import { API, enc, gcsRequest } from "./gcs.js";
import { isSafeRelKey, joinKey } from "./paths.js";

export class CloudStorageError extends Error {
  constructor(message, { status = null, provider = null } = {}) {
    super(message);
    this.name = "CloudStorageError";
    this.status = status;
    this.provider = provider;
  }
}

/** Mirrors the bucket layout onto a local directory — dev / offline stand-in. */
export class LocalStorageProvider {
  constructor(rootDir) {
    if (!rootDir) throw new CloudStorageError("local provider needs a root dir", { provider: "local" });
    this.rootDir = rootDir;
  }

  fileFor(key) {
    if (!isSafeRelKey(key)) throw new CloudStorageError(`unsafe key: ${key}`, { provider: "local" });
    return path.join(this.rootDir, joinKey(key));
  }

  async upload(key, data) {
    const file = this.fileFor(key);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const tmp = `${file}.tmp-${process.pid}`;
    fs.writeFileSync(tmp, Buffer.isBuffer(data) ? data : Buffer.from(String(data)));
    fs.renameSync(tmp, file);
    return { key, size: fs.statSync(file).size };
  }

  async download(key) {
    const file = this.fileFor(key);
    if (!fs.existsSync(file)) throw new CloudStorageError(`not found: ${key}`, { status: 404, provider: "local" });
    return fs.readFileSync(file);
  }

  async exists(key) {
    return fs.existsSync(this.fileFor(key));
  }

  async delete(key) {
    try {
      fs.unlinkSync(this.fileFor(key));
    } catch (err) {
      if (err.code !== "ENOENT") throw new CloudStorageError(err.message, { provider: "local" });
    }
  }

  async list(prefix = "") {
    const base = path.join(this.rootDir, joinKey(prefix));
    const out = [];
    const walk = (dir) => {
      if (!fs.existsSync(dir)) return;
      for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, ent.name);
        if (ent.isDirectory()) walk(p);
        else {
          const st = fs.statSync(p);
          out.push({ key: path.relative(this.rootDir, p).replace(/\\/g, "/"), size: st.size, updated: st.mtime.toISOString() });
        }
      }
    };
    walk(base);
    return out;
  }
}

/** Google Cloud Storage via the JSON API (see gcs.js for auth). */
export class GcpStorageProvider {
  constructor(cfg) {
    if (!cfg.gcp.bucket) throw new CloudStorageError("gcp provider needs CAB_GCP_BUCKET", { provider: "gcp" });
    this.cfg = cfg;
    this.bucket = cfg.gcp.bucket;
  }

  async upload(key, data, { contentType = "application/octet-stream" } = {}) {
    const buf = Buffer.isBuffer(data) ? data : Buffer.from(String(data));
    try {
      await gcsRequest(this.cfg, {
        method: "POST",
        url: `${API}/upload/storage/v1/b/${this.bucket}/o?uploadType=media&name=${enc(key)}`,
        headers: { "Content-Type": contentType, "Content-Length": String(buf.length) },
        body: buf,
      });
      return { key, size: buf.length };
    } catch (err) {
      throw new CloudStorageError(err.message, { status: err.status, provider: "gcp" });
    }
  }

  async download(key) {
    try {
      return await gcsRequest(this.cfg, {
        url: `${API}/storage/v1/b/${this.bucket}/o/${enc(key)}?alt=media`,
        raw: true,
      });
    } catch (err) {
      throw new CloudStorageError(err.message, { status: err.status, provider: "gcp" });
    }
  }

  async exists(key) {
    try {
      await gcsRequest(this.cfg, {
        url: `${API}/storage/v1/b/${this.bucket}/o/${enc(key)}?fields=name`,
      });
      return true;
    } catch (err) {
      if (err.status === 404) return false;
      throw new CloudStorageError(err.message, { status: err.status, provider: "gcp" });
    }
  }

  async delete(key) {
    try {
      await gcsRequest(this.cfg, {
        method: "DELETE",
        url: `${API}/storage/v1/b/${this.bucket}/o/${enc(key)}`,
      });
    } catch (err) {
      if (err.status === 404) return;
      throw new CloudStorageError(err.message, { status: err.status, provider: "gcp" });
    }
  }

  async list(prefix = "") {
    const out = [];
    let pageToken = "";
    try {
      do {
        const data = await gcsRequest(this.cfg, {
          url: `${API}/storage/v1/b/${this.bucket}/o?prefix=${encodeURIComponent(prefix)}&fields=items(name,size,updated),nextPageToken${pageToken ? `&pageToken=${pageToken}` : ""}`,
        });
        for (const it of data.items || []) out.push({ key: it.name, size: Number(it.size) || 0, updated: it.updated });
        pageToken = data.nextPageToken || "";
      } while (pageToken);
    } catch (err) {
      throw new CloudStorageError(err.message, { status: err.status, provider: "gcp" });
    }
    return out;
  }
}

/** Config → provider. Returns null when cloud is disabled. */
export function createStorageProvider(cfg) {
  if (!cfg || !cfg.enabled) return null;
  if (cfg.provider === "local") return new LocalStorageProvider(cfg.localRoot);
  if (cfg.provider === "gcp") return new GcpStorageProvider(cfg);
  throw new CloudStorageError(`unknown provider: ${cfg.provider}`);
}
