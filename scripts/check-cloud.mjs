/** Cloud storage layer checks — offline parts always run; the real-GCS
 *  roundtrip only runs when the env opts in:
 *    CAB_CLOUD_ENABLED=1 CAB_CLOUD_PROVIDER=gcp CAB_GCP_BUCKET=<bucket>
 *    node scripts/check-cloud.mjs
 */
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const cloudDir = join(root, "..", "src", "cloud");
const { loadCloudConfig } = await import(pathToFileURL(join(cloudDir, "config.js")).href);
const { cloudKey, isSafeRelKey } = await import(pathToFileURL(join(cloudDir, "paths.js")).href);
const { LocalStorageProvider, createStorageProvider } = await import(pathToFileURL(join(cloudDir, "providers.js")).href);
const cloud = await import(pathToFileURL(join(cloudDir, "index.js")).href);

const errors = [];
function check(cond, msg) {
  if (!cond) errors.push(msg);
}

// Snapshot the caller's env before the local-provider section mutates it.
const wantGcs = process.env.CAB_CLOUD_PROVIDER === "gcp" && !!process.env.CAB_GCP_BUCKET;

// --- config ---
let cfg = loadCloudConfig({});
check(cfg.enabled === false, "default disabled");
check(cfg.provider === "gcp", "default provider");
check(cfg.root === "omnicam", "default root omnicam");
check(cfg.gcp.region === "asia-southeast1", "default region");

cfg = loadCloudConfig({
  CAB_CLOUD_ENABLED: "1",
  CAB_CLOUD_PROVIDER: "local",
  CAB_CLOUD_LOCAL_ROOT: "/tmp/x",
  CAB_GCP_BUCKET: "b",
});
check(cfg.enabled === true, "enabled");
check(cfg.provider === "local", "local provider");
check(cfg.localRoot === "/tmp/x", "localRoot");

// --- path rules ---
check(cloudKey("omnicam", "jobs/a.db") === "omnicam/jobs/a.db", "key join");
check(cloudKey("shared/cnjob", "x.cnjob") === "shared/cnjob/x.cnjob", "shared root");
let threw = 0;
try { cloudKey("jobs", "a.db"); } catch { threw++; }
try { cloudKey("omnicam", "../escape"); } catch { threw++; }
try { cloudKey("omnicam", "C:/abs"); } catch { threw++; }
check(threw === 3, "unsafe roots/keys rejected");
check(isSafeRelKey("a/b/c.txt") === true, "safe rel");
check(isSafeRelKey("/abs") === false, "abs rejected");
check(isSafeRelKey("a/../b") === false, "dotdot rejected");

// --- local provider roundtrip ---
const dir = fs.mkdtempSync(path.join(os.tmpdir(), "omnicam-local-"));
const local = new LocalStorageProvider(dir);
const payload = `omnicam test ${Date.now()}`;
await local.upload("omnicam/jobs/t.db", payload);
check(await local.exists("omnicam/jobs/t.db"), "local exists");
check((await local.download("omnicam/jobs/t.db")).toString() === payload, "local content");
check((await local.list("omnicam/")).some((i) => i.key === "omnicam/jobs/t.db"), "local list");
await local.delete("omnicam/jobs/t.db");
check(!(await local.exists("omnicam/jobs/t.db")), "local delete");
try { await local.upload("../bad", "x"); errors.push("unsafe upload accepted"); } catch { /* expected */ }
fs.rmSync(dir, { recursive: true, force: true });

// --- facade: disabled → null result, no throw ---
delete process.env.CAB_CLOUD_ENABLED;
delete process.env.CAB_CLOUD_CONFIG;
cloud._reset();
check((await cloud.uploadText("omnicam", "x.txt", "hi")) === null, "disabled sync is null");
check(cloud.status().enabled === false, "status disabled");

// --- facade over local provider ---
const dir2 = fs.mkdtempSync(path.join(os.tmpdir(), "omnicam-cloud-"));
process.env.CAB_CLOUD_ENABLED = "1";
process.env.CAB_CLOUD_PROVIDER = "local";
process.env.CAB_CLOUD_LOCAL_ROOT = dir2;
cloud._reset();
const up = await cloud.uploadText("omnicam/jobs", "job.db", "db-bytes");
check(up?.ok === true && up.key === "omnicam/jobs/job.db", "facade upload");
const down = await cloud.downloadText("omnicam", "jobs/job.db");
check(down?.text === "db-bytes", "facade download");
check((await cloud.listPrefix("omnicam", "jobs"))?.items?.length === 1, "facade list");
check((await cloud.deleteKey("omnicam", "jobs/job.db"))?.ok === true, "facade delete");
check(cloud.status().lastSync?.ok === true, "lastSync ok");
check((await cloud.downloadText("omnicam", "jobs/job.db"))?.ok === false, "missing errors cleanly");
fs.rmSync(dir2, { recursive: true, force: true });

// --- real GCS roundtrip (opt-in) ---
if (wantGcs) {
  process.env.CAB_CLOUD_ENABLED = "1";
  process.env.CAB_CLOUD_PROVIDER = "gcp";
  cloud._reset();
  const rel = `storage-test-${Date.now()}.txt`;
  const text = `gcs roundtrip ${Date.now()}`;
  const u = await cloud.uploadText("temp", rel, text);
  check(u?.ok === true, `gcs upload: ${u?.error || ""}`);
  const d = await cloud.downloadText("temp", rel);
  check(d?.text === text, "gcs content matches");
  const l = await cloud.listPrefix("temp", "storage-test-");
  check(l?.items?.some((i) => i.key === `temp/${rel}`), "gcs list");
  check((await cloud.deleteKey("temp", rel))?.ok === true, "gcs delete");
  check(cloud.status().provider === "gcp", "status gcp");
  if (!errors.length) console.log("gcs roundtrip ok:", `temp/${rel}`);
} else {
  console.log("gcs roundtrip skipped (set CAB_CLOUD_PROVIDER=gcp + CAB_GCP_BUCKET)");
}

if (errors.length) {
  console.error("FAIL", errors);
  process.exit(1);
}
console.log("OK cloud");
