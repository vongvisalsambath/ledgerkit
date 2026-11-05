#!/usr/bin/env bash
# Vercel install step: the .NET SDK.
set -euo pipefail
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /tmp/dotnet
