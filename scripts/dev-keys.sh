#!/usr/bin/env bash
# Generate DEVELOPMENT-ONLY signing and encryption key material into .secrets/ (git-ignored).
#
#   .secrets/signing.crt      .secrets/signing.key       (keyUsage: digitalSignature)
#   .secrets/encryption.crt   .secrets/encryption.key    (keyUsage: keyEncipherment)
#
# Self-signed RSA-2048 certificates + PKCS#8 private keys, PEM encoded. OpenIddict validates the
# X.509 key usage, hence the explicit keyUsage extension on each certificate.
# Refuses to overwrite existing files: regenerating the signing key invalidates every issued token.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dir="$root/.secrets"

for name in signing encryption; do
  for ext in crt key; do
    if [[ -e "$dir/$name.$ext" ]]; then
      echo "error: $dir/$name.$ext already exists; refusing to overwrite (delete it deliberately to regenerate)." >&2
      exit 1
    fi
  done
done

mkdir -p "$dir"

generate() {
  local name="$1" usage="$2"
  openssl req -x509 -newkey rsa:2048 -nodes -days 3650 \
    -subj "/CN=auth-core-dev-$name" \
    -addext "keyUsage=critical,$usage" \
    -keyout "$dir/$name.key" -out "$dir/$name.crt" 2>/dev/null
  # DEV ONLY: world-readable so the non-root container user can read a bind mount of this directory.
  # Production uses orchestrator secrets (Docker/Kubernetes) with proper ownership and tighter modes.
  chmod 0644 "$dir/$name.key" "$dir/$name.crt"
}

generate signing digitalSignature
generate encryption keyEncipherment

echo "Wrote development keys to $dir (git-ignored). Do not use them in production."
