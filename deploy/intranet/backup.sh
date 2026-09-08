#!/usr/bin/env bash
# Consistent backup of the intranet stack: PostgreSQL (pg_dump, custom format) + MinIO data volume (tar).
# Usage: ./backup.sh [target-dir]      (run from deploy/intranet with the stack running)
# Produces <target-dir>/cabinetnc-<UTC timestamp>/{postgres.dump,minio-data.tgz,manifest.txt}
set -euo pipefail
cd "$(dirname "$0")"
target_root="${1:-./backups}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="$target_root/cabinetnc-$stamp"
mkdir -p "$target"

# shellcheck disable=SC1091
set -a; source ./.env; set +a
db="${POSTGRES_DB:-cabinetnc}"; user="${POSTGRES_USER:-cabinetnc}"
project="$(docker compose config --format json | sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' | head -n1)"
project="${project:-cabinetnc-intranet}"

echo "backing up database $db ..."
docker compose exec -T postgres pg_dump -U "$user" -d "$db" --format=custom --no-owner > "$target/postgres.dump"

echo "backing up object store volume ${project}_minio-data ..."
# pwd -W gives a Windows path under Git Bash (developer machines); plain pwd on the Ubuntu server.
host_target="$(cd "$target" && (pwd -W 2>/dev/null || pwd))"
MSYS_NO_PATHCONV=1 docker run --rm -v "${project}_minio-data:/data:ro" -v "$host_target:/out" alpine:3.20 \
  tar czf /out/minio-data.tgz -C /data .

{
  echo "createdUtc=$stamp"
  echo "project=$project"
  echo "database=$db"
  echo "apiImage=$(docker compose images cabinetnc-api --format json 2>/dev/null | sed -n 's/.*"ID": *"\([^"]*\)".*/\1/p' | head -n1)"
  echo "gitRevision=${SOURCE_REVISION:-unknown}"
  echo "postgresDumpBytes=$(stat -c %s "$target/postgres.dump")"
  echo "minioTgzBytes=$(stat -c %s "$target/minio-data.tgz")"
} > "$target/manifest.txt"

echo "backup written to $target"
cat "$target/manifest.txt"
