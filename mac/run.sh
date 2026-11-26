#!/usr/bin/env bash
# Jalankan langsung dari kode sumber (untuk mencoba / mengembangkan).
set -euo pipefail
cd "$(dirname "$0")"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "❌ .NET 8 SDK belum terpasang. Pasang dengan:  brew install --cask dotnet-sdk"
  exit 1
fi
dotnet run --project DepupOffice.Mac/DepupOffice.Mac.csproj -c Release -- "$@"
