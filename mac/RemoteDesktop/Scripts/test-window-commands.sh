#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/window-commands.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

swiftc -swift-version 6 -suppress-warnings -parse-as-library \
    "$project_dir/Sources/RemoteDesktop/Protocol.swift" \
    "$project_dir/Sources/RemoteDesktop/Glass.swift" \
    "$project_dir/Sources/RemoteDesktop/WindowActionsView.swift" \
    "$project_dir/Scripts/WindowCommandsHarness.swift" \
    -o "$build_dir/window-commands-harness"
"$build_dir/window-commands-harness"
