#!/usr/bin/env bash
# Restore a backup made by backup.sh into a running stack. DESTRUCTIVE: replaces the database and the
# object store content. Usage: ./restore.sh <backup-dir>
# Procedure: stop api + workers -> drop & recreate schema -> pg_restore -> replace MinIO data -> start.
set -euo pipefail
cd "$(dirname "$0")"
backup="${1:?usage: restore.sh <backup-dir>}"
[[ -f "$backup/postgres.dump" && -f "$backup/minio-data.tgz" ]] || { echo "not a backup dir: $backup" >&2; exit 1; }

# shellcheck disable=SC1091
set -a; source ./.env; set +a
db="${POSTGRES_DB:-cabinetnc}"; user="${POSTGRES_USER:-cabinetnc}"
project="$(docker compose config --format json | sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' | head -n1)"
project="${project:-cabinetnc-intranet}"

echo "stopping api and workers ..."
docker compose stop cabinetnc-api cabinetnc-worker cabinetnc-worker-2 reverse-proxy

echo "restoring database $db ..."
docker compose exec -T postgres psql -U "$user" -d "$db" -v ON_ERROR_STOP=1 -c 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;'
docker compose exec -T postgres pg_restore -U "$user" -d "$db" --no-owner --exit-on-error < "$backup/postgres.dump"

echo "restoring object store volume ${project}_minio-data ..."
docker compose stop minio
host_backup="$(cd "$backup" && (pwd -W 2>/dev/null || pwd))"
MSYS_NO_PATHCONV=1 docker run --rm -v "${project}_minio-data:/data" -v "$host_backup:/in:ro" alpine:3.20 \
  sh -c 'find /data -mindepth 1 -delete && tar xzf /in/minio-data.tgz -C /data'
docker compose start minio

echo "starting api and workers ..."
docker compose up -d cabinetnc-api cabinetnc-worker cabinetnc-worker-2 reverse-proxy
echo "restore complete; verify with: curl -sS http://127.0.0.1:8080/api/v1/health/ready (or via the proxy)"
