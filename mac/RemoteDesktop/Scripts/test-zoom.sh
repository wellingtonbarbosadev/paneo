#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/zoom.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

swiftc -swift-version 6 -suppress-warnings -parse-as-library \
    "$project_dir/Sources/RemoteDesktop/Protocol.swift" \
    "$project_dir/Sources/RemoteDesktop/H264Decoder.swift" \
    "$project_dir/Sources/RemoteDesktop/JpegDecoder.swift" \
    "$project_dir/Sources/RemoteDesktop/RemoteVideoInteraction.swift" \
    "$project_dir/Sources/RemoteDesktop/AppMotion.swift" \
    "$project_dir/Sources/RemoteDesktop/RemoteVideoView.swift" \
    "$project_dir/Sources/RemoteDesktop/AppMenus.swift" \
    "$project_dir/Scripts/ZoomHarness.swift" -o "$build_dir/zoom-harness"
"$build_dir/zoom-harness"
