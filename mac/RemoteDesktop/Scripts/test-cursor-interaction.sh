#!/bin/sh
set -eu

project_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
build_dir=$(mktemp -d "${TMPDIR:-/tmp}/cursor-interaction.XXXXXX")
trap 'rm -rf "$build_dir"' EXIT INT TERM

set --
for source in "$project_dir"/Sources/RemoteDesktop/*.swift; do
    case "$(basename -- "$source")" in
        RemoteDesktopApp.swift|AppMenus.swift) continue ;;
    esac
    set -- "$@" "$source"
done

swiftc -swift-version 6 -suppress-warnings -parse-as-library "$@" "$project_dir/Scripts/CursorInteractionHarness.swift" -o "$build_dir/cursor-harness"
"$build_dir/cursor-harness"
