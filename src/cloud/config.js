/** Cloud storage configuration — same variable names and JSON file keys as
 *  Cab Lab (the-cab-lab/cloud/config.js). Keep both repos in sync.
 *
 *  Sources, lowest precedence first:
 *    1. JSON file at CAB_CLOUD_CONFIG (optional local file, never committed)
 *    2. environment variables
 *
 *  Node-side module: this directory is consumed by scripts/check-*.mjs and the
 *  desktop host, not bundled into the browser app by Vite (node: imports).
 */
import fs from "node:fs";
import path from "node:path";
import os from "node:os";

const TRUE_VALUES = new Set(["1", "true", "yes", "on"]);

function readJsonFile(file) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8"));
  } catch {
    return {};
  }
}

function pick(env, file, envName, fileKey) {
  const v = env[envName];
  if (v !== undefined && v !== "") return v;
  return file[fileKey];
}

export function loadCloudConfig(env = process.env) {
  const file = env.CAB_CLOUD_CONFIG ? readJsonFile(env.CAB_CLOUD_CONFIG) : {};
  const enabled = TRUE_VALUES.has(String(pick(env, file, "CAB_CLOUD_ENABLED", "enabled") || "").toLowerCase());
  const provider = String(pick(env, file, "CAB_CLOUD_PROVIDER", "provider") || "gcp").toLowerCase();
  return {
    enabled,
    provider,
    root: String(pick(env, file, "CAB_CLOUD_ROOT", "root") || "omnicam").replace(/^\/+|\/+$/g, ""),
    gcp: {
      projectId: pick(env, file, "CAB_GCP_PROJECT_ID", "gcpProjectId") || null,
      bucket: pick(env, file, "CAB_GCP_BUCKET", "gcpBucket") || null,
      region: pick(env, file, "CAB_GCP_REGION", "gcpRegion") || "asia-southeast1",
      credentialsFile: pick(env, file, "CAB_GCP_CREDENTIALS", "gcpCredentialsFile") || env.GOOGLE_APPLICATION_CREDENTIALS || null,
      accessToken: pick(env, file, "CAB_GCP_ACCESS_TOKEN", "gcpAccessToken") || null,
    },
    localRoot: pick(env, file, "CAB_CLOUD_LOCAL_ROOT", "localRoot")
      || path.join(os.tmpdir(), "cab-cloud-local"),
  };
}
