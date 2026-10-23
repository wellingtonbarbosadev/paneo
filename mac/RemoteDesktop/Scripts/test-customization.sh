#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/customization.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

set --
for source in "$project_dir"/Sources/RemoteDesktop/*.swift; do
    case "$(basename -- "$source")" in
        RemoteDesktopApp.swift|AppMenus.swift) continue ;;
    esac
    set -- "$@" "$source"
done

swiftc -swift-version 6 -suppress-warnings -parse-as-library "$@" "$project_dir/Scripts/CustomizationHarness.swift" -o "$build_dir/customization-harness"
"$build_dir/customization-harness"
