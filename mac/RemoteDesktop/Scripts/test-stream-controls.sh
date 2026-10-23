#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/stream-controls.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

swiftc -swift-version 6 -suppress-warnings -parse-as-library \
    "$project_dir/Sources/RemoteDesktop/StreamPreferences.swift" \
    "$project_dir/Sources/RemoteDesktop/RemoteConnectionFailure.swift" \
    "$project_dir/Sources/RemoteDesktop/RemoteWindowRecovery.swift" \
    "$project_dir/Sources/RemoteDesktop/Glass.swift" \
    "$project_dir/Sources/RemoteDesktop/AppSettings.swift" \
    "$project_dir/Sources/RemoteDesktop/AppMotion.swift" \
    "$project_dir/Sources/RemoteDesktop/StreamControlsView.swift" \
    "$project_dir/Sources/RemoteDesktop/QuickSearchPalette.swift" \
    "$project_dir/Scripts/StreamControlsHarness.swift" \
    -o "$build_dir/stream-controls-harness"
"$build_dir/stream-controls-harness"
