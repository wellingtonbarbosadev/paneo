#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../../.." && pwd)
mac_dir="$repo_root/mac/RemoteDesktop"
if [ "$#" -gt 1 ]; then
    echo "uso: $0 [caminho/video-media.bin]" >&2
    exit 2
fi
input=${1:-"$repo_root/artifacts/video-media.bin"}
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/stream-decoder.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

test -f "$input"
swiftc -swift-version 6 -suppress-warnings -parse-as-library \
    "$mac_dir/Sources/RemoteDesktop/Protocol.swift" \
    "$mac_dir/Sources/RemoteDesktop/H264Decoder.swift" \
    "$mac_dir/Scripts/StreamDecoderHarness.swift" \
    -o "$build_dir/stream-decoder-harness"
"$build_dir/stream-decoder-harness" "$input"
