#!/usr/bin/env bash
# Runs the whole test suite inside the Jellyfin server image the plugin targets, so the ffmpeg integration tests
# (sync, colour, seams, standby) exercise that release's own jellyfin-ffmpeg instead of returning early for want
# of one. LIVECHANNELS_REQUIRE_FFMPEG makes a missing ffmpeg fail the run rather than pass it silently.
#
# Usage: .github/scripts/test-in-jellyfin.sh [jellyfin image]   (needs Docker; run from the repository root)
set -euo pipefail

IMAGE="${1:-jellyfin/jellyfin:12.4}"
SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
SDK_VOLUME="lc-test-dotnet-sdk"

# The Jellyfin image has no .NET SDK: copy one into a volume and mount it read-only.
docker volume create "${SDK_VOLUME}" >/dev/null
docker run --rm -v "${SDK_VOLUME}:/out" "${SDK_IMAGE}" bash -c 'test -x /out/dotnet || cp -a /usr/share/dotnet/. /out/'

# Tests run as an unprivileged user (permission tests are meaningless as root) on a private copy of the tree.
docker run --rm --entrypoint bash \
  -v "${SDK_VOLUME}:/opt/dotnet:ro" \
  -v "$(pwd):/repo:ro" \
  -e LIVECHANNELS_REQUIRE_FFMPEG=1 \
  "${IMAGE}" -c '
    set -euo pipefail
    /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -version | head -1
    useradd -m tester
    cp -a /repo /home/tester/src
    rm -rf /home/tester/src/*/bin /home/tester/src/*/obj
    chown -R tester /home/tester/src
    su tester -c "export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:\$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 LIVECHANNELS_REQUIRE_FFMPEG=1
      cd /home/tester/src && dotnet test --configuration Release --logger \"console;verbosity=minimal\""
  '
