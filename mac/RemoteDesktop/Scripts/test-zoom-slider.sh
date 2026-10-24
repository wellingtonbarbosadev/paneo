#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
repo_dir=$(CDPATH= cd -- "$project_dir/../.." && pwd)
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/zoom-slider.XXXXXX")
artifacts_dir="$repo_dir/artifacts/zoom-slider"
trap 'rm -rf "$build_dir"' EXIT INT TERM
mkdir -p "$artifacts_dir"

set --
for source in "$project_dir"/Sources/RemoteDesktop/*.swift; do
    case "${source##*/}" in
        RemoteDesktopApp.swift|AppMenus.swift) continue ;;
    esac
    set -- "$@" "$source"
done

swiftc -swift-version 6 -suppress-warnings -parse-as-library \
    "$@" "$project_dir/Scripts/ZoomSliderHarness.swift" -o "$build_dir/zoom-slider-harness"
if ! "$build_dir/zoom-slider-harness" "$artifacts_dir" >"$build_dir/harness.log" 2>&1; then
    cat "$build_dir/harness.log"
    exit 1
fi
cat "$build_dir/harness.log"
if ! rg -q '^PASS: slider,' "$build_dir/harness.log"; then
    echo "FAIL: harness saiu sem completar todas as fases"
    exit 1
fi
