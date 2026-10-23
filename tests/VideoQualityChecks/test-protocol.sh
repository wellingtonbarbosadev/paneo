#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
swiftc -parse-as-library \
  "$root/mac/RemoteDesktop/Sources/RemoteDesktop/Protocol.swift" \
  "$root/mac/RemoteDesktop/Sources/RemoteDesktop/StreamPreferences.swift" \
  "$root/mac/RemoteDesktop/Sources/RemoteDesktop/RemoteConnectionFailure.swift" \
  "$root/tests/VideoQualityChecks/ProtocolHarness.swift" \
  -o "$tmp/protocol-check"
"$tmp/protocol-check"
