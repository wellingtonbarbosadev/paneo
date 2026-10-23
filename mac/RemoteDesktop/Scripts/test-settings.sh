#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/settings.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

swiftc -swift-version 6 -suppress-warnings -parse-as-library \
    "$project_dir/Sources/RemoteDesktop/AppSettings.swift" \
    "$project_dir/Sources/RemoteDesktop/Glass.swift" \
    "$project_dir/Sources/RemoteDesktop/AppMotion.swift" \
    "$project_dir/Sources/RemoteDesktop/SettingsViewController.swift" \
    "$project_dir/Scripts/SettingsHarness.swift" \
    -o "$build_dir/settings-harness"
"$build_dir/settings-harness"
