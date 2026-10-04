#!/bin/sh
# Generates the master key (AES-256 key that encrypts secrets and the internal CA key) on first start and keeps it in /data.
# Set AETHERA_MASTER_KEY yourself to manage it elsewhere. LOSING THIS KEY MAKES STORED SECRETS UNREADABLE: back it up.
set -eu
if [ -z "${AETHERA_MASTER_KEY:-}" ]; then
  f=/data/master.key
  if [ ! -s "$f" ]; then
    umask 077
    head -c 32 /dev/urandom | base64 > "$f"
    echo "aethera: generated a new master key in $f (back it up)" >&2
  fi
  AETHERA_MASTER_KEY="$(cat "$f")"
  export AETHERA_MASTER_KEY
fi
exec "$@"
