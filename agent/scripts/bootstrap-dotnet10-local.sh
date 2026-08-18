#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
INSTALL_DIR="${DOTNET_INSTALL_DIR:-$ROOT/.dotnet}"
SDK_VERSION="${DOTNET_SDK_VERSION:-10.0.302}"
CACHE_DIR="${DOTNET_BOOTSTRAP_CACHE:-$ROOT/.bootstrap-cache}"
mkdir -p "$INSTALL_DIR" "$CACHE_DIR"
SCRIPT="$CACHE_DIR/dotnet-install.sh"
if [[ ! -f "$SCRIPT" ]]; then
  curl --fail --show-error --location --proto '=https' --tlsv1.2 \
    https://dot.net/v1/dotnet-install.sh -o "$SCRIPT"
  chmod +x "$SCRIPT"
fi
"$SCRIPT" --version "$SDK_VERSION" --install-dir "$INSTALL_DIR" --no-path
printf 'Local SDK installed. Use:\n  export DOTNET_ROOT=%q\n  export PATH="$DOTNET_ROOT:$PATH"\n' "$INSTALL_DIR"
"$INSTALL_DIR/dotnet" --info
