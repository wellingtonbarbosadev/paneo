#!/bin/sh
# Gera Paneo.app e .dmg locais para uso proprio neste Mac.
# Assinatura local de desenvolvimento: sem Developer ID, sem notarizacao, sem
# distribuicao aprovada. Somente build da arquitetura local (uname -m).
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
packaging_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
repo_dir=$(CDPATH= cd -- "$packaging_dir/.." && pwd)
package_dir="$repo_dir/mac/RemoteDesktop"
plist_template="$script_dir/Info.plist.in"
version_file="$packaging_dir/VERSION"

fail() { echo "erro: $*" >&2; exit 1; }

for tool in swift hdiutil codesign plutil sed tr grep security; do
	command -v "$tool" >/dev/null 2>&1 || fail "ferramenta nao encontrada: $tool"
done
[ -f "$version_file" ] || fail "arquivo VERSION ausente: $version_file"
[ -f "$plist_template" ] || fail "template ausente: $plist_template"
[ -d "$package_dir" ] || fail "pacote Swift ausente: $package_dir"
[ -f "$repo_dir/assets/branding/Paneo.icns" ] || fail "icone Paneo.icns ausente"

version=$(tr -d ' \t\r\n' < "$version_file")
[ -n "$version" ] || fail "packaging/VERSION vazio"
printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' || fail "packaging/VERSION fora do formato x.y.z: $version"

signing_identity=${PANEO_CODESIGN_IDENTITY-Paneo Development}
if [ -z "$signing_identity" ]; then
	fail "PANEO_CODESIGN_IDENTITY vazio; use Paneo Development ou uma identidade local válida"
fi
if [ "$signing_identity" != "-" ]; then
	identities=$(security find-identity -p codesigning 2>/dev/null) ||
		fail "nao foi possivel consultar identidades de assinatura no Chaves"
	printf '%s\n' "$identities" | grep -Fq "\"$signing_identity\"" ||
		fail "identidade '$signing_identity' ausente entre as identidades de assinatura do Chaves; configure-a ou defina PANEO_CODESIGN_IDENTITY explicitamente (use '-' apenas para ad-hoc)"
fi

arch=$(uname -m)
app_name="Paneo"
binary_name="RemoteDesktop"
out_dir="$script_dir/dist"
dmg_name="Paneo-$version-mac-$arch.dmg"

echo "== Build release (SwiftPM, arquitetura local $arch)"
swift build -c release --package-path "$package_dir"
bin_path=$(swift build -c release --package-path "$package_dir" --show-bin-path)
binary="$bin_path/$binary_name"
[ -x "$binary" ] || fail "binario nao encontrado: $binary"

echo "== Bundle $app_name.app (versao $version)"
rm -rf "$out_dir"
app_bundle="$out_dir/$app_name.app"
mkdir -p "$app_bundle/Contents/MacOS" "$app_bundle/Contents/Resources"
cp "$binary" "$app_bundle/Contents/MacOS/$binary_name"
cp "$repo_dir/assets/branding/Paneo.icns" "$app_bundle/Contents/Resources/Paneo.icns"
sed -e "s|@VERSION@|$version|g" "$plist_template" > "$app_bundle/Contents/Info.plist"
plutil -lint "$app_bundle/Contents/Info.plist" >/dev/null || fail "Info.plist invalido"

if [ "$signing_identity" = "-" ]; then
	echo "== Assinatura ad-hoc local explicitamente solicitada"
else
	echo "== Assinatura local com identidade: $signing_identity"
fi
codesign --force --sign "$signing_identity" "$app_bundle"
codesign --verify --strict --verbose=2 "$app_bundle"

echo "== DMG $dmg_name"
dmg_root="$out_dir/.dmg-root"
mkdir -p "$dmg_root"
cp -R "$app_bundle" "$dmg_root/$app_name.app"
dmg_path="$out_dir/$dmg_name"
hdiutil create -volname "$app_name $version" -srcfolder "$dmg_root" -ov -format UDZO "$dmg_path" >/dev/null
rm -rf "$dmg_root"
hdiutil verify "$dmg_path" >/dev/null
codesign --force --sign "$signing_identity" "$dmg_path" >/dev/null
codesign --verify --strict --verbose=2 "$dmg_path" >/dev/null

echo "ok: $app_bundle"
echo "ok: $dmg_path"
echo "nota: assinatura local sem Developer ID nem notarizacao; Gatekeeper pode bloquear copias baixadas de outra maquina."
echo "nota: assinatura estavel nao garante ausencia de prompts do Keychain; sem Team ID Apple, o partition_id pode depender do cdhash de cada build."
