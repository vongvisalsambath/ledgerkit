#!/usr/bin/env bash
# Vercel install step: the .NET SDK, and Iso20022Kit at a pinned commit (LedgerKit reads camt.053 through it).
set -euo pipefail
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /tmp/dotnet
rm -rf .deps/iso20022kit
git clone --quiet https://github.com/vongvisalsambath/iso20022kit .deps/iso20022kit
git -C .deps/iso20022kit checkout --quiet 597b3f2c1b95f7b0bb9c6c34c5b42d32281d1692
