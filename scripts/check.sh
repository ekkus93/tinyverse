#!/usr/bin/env bash
# The checks every change must pass before it is merged: Rust formatting,
# lints and tests, and that the generated C# protocol code is up to date.
# Unity is not started here; see scripts/unity.sh for client checks.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "== cargo fmt"
cargo fmt --manifest-path "$root/server/Cargo.toml" --all -- --check

echo "== cargo clippy"
cargo clippy --manifest-path "$root/server/Cargo.toml" --workspace --all-targets -- -D warnings

echo "== cargo test"
cargo test --manifest-path "$root/server/Cargo.toml" --workspace

echo "== generated C# is current"
generated="$root/client/Assets/Tinyverse/Proto/Generated"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
"$root/scripts/gen-proto.sh" "$tmp" >/dev/null
if ! diff -r --exclude='*.meta' "$tmp" "$generated" >/dev/null; then
  echo "generated C# in $generated is stale; run scripts/gen-proto.sh" >&2
  diff -r --exclude='*.meta' --brief "$tmp" "$generated" >&2 || true
  exit 1
fi

echo "all checks passed"
