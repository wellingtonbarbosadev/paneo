#!/bin/sh
# Confere consistencia de versao e nomes entre packaging/VERSION, o template de
# Info.plist do Mac, o script de publicacao do Windows, o .iss do Inno Setup e
# docs/INSTALLERS.md. Nao executa build; le apenas arquivos do escopo autorizado
# mais Package.swift e o csproj (somente leitura).
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
packaging_dir="$script_dir"
repo_dir=$(CDPATH= cd -- "$packaging_dir/.." && pwd)

plist="$packaging_dir/mac/Info.plist.in"
publish_ps1="$packaging_dir/windows/publish-win-x64.ps1"
installer_ps1="$packaging_dir/windows/build-installer.ps1"
iss="$packaging_dir/windows/installer/remoteDesktop-host.iss"
doc="$repo_dir/docs/INSTALLERS.md"
package_swift="$repo_dir/mac/RemoteDesktop/Package.swift"
csproj="$repo_dir/windows/WindowProbe/WindowProbe.csproj"

fail_count=0
ok() { printf '%s\n' "ok: $*"; }
err() { printf '%s\n' "erro: $*" >&2; fail_count=$((fail_count + 1)); }

for f in "$plist" "$publish_ps1" "$installer_ps1" "$iss" "$doc" "$package_swift" "$csproj"; do
	[ -f "$f" ] || err "arquivo ausente: $f"
done
[ "$fail_count" -eq 0 ] || { echo "abortando: faltam arquivos." >&2; exit 1; }

version=$(tr -d ' \t\r\n' < "$packaging_dir/VERSION")
if printf '%s' "$version" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
	ok "packaging/VERSION = $version (formato x.y.z)"
else
	err "packaging/VERSION fora do formato x.y.z: '$version'"
	version=""
fi

# --- Mac: template do Info.plist e minimo do SwiftPM -------------------------
if grep -q '<key>CFBundleShortVersionString</key>' "$plist" && grep -q '@VERSION@' "$plist"; then
	ok "Info.plist.in usa @VERSION@ como versao (fonte unica VERSION)"
else
	err "Info.plist.in nao referencia @VERSION@"
fi
if grep -q '<string>RemoteDesktop</string>' "$plist" && grep -q '<key>CFBundleExecutable</key>' "$plist"; then
	ok "Info.plist.in: bundle RemoteDesktop com executavel RemoteDesktop"
else
	err "Info.plist.in: executavel/bundle RemoteDesktop inconsistente"
fi
if grep -q '<string>dev.remotedesktop.client</string>' "$plist"; then
	ok "Info.plist.in: CFBundleIdentifier dev.remotedesktop.client"
else
	err "Info.plist.in: CFBundleIdentifier diferente do documentado"
fi
if grep -q '<string>14.0</string>' "$plist" && grep -q '\.macOS(\.v14)' "$package_swift"; then
	ok "macOS minimo 14.0 coerente entre Info.plist.in e Package.swift"
else
	err "macOS minimo divergente entre Info.plist.in (14.0) e Package.swift (.macOS(.v14))"
fi

# --- Windows: publicacao self-contained -------------------------------------
if grep -q 'WindowProbe.csproj' "$publish_ps1"; then
	ok "publish-win-x64.ps1 publica windows/WindowProbe/WindowProbe.csproj"
else
	err "publish-win-x64.ps1 nao referencia WindowProbe.csproj"
fi
if grep -q -- "--self-contained" "$publish_ps1" && grep -q -- "'true'" "$publish_ps1"; then
	ok "publish-win-x64.ps1 usa --self-contained true"
else
	err "publish-win-x64.ps1 sem --self-contained true"
fi
if grep -q "Join-Path 'publish' \$Runtime" "$publish_ps1" && grep -q "'win-x64'" "$publish_ps1"; then
	ok "publish-win-x64.ps1: saida publish/win-x64 padrao"
else
	err "publish-win-x64.ps1: padrao de saida publish/win-x64 nao encontrado"
fi

# --- Windows: instalador por usuario (sem admin/firewall) -------------------
iss_version=$(sed -n 's/^#define MyAppVersion "\([0-9.]*\)"$/\1/p' "$iss")
if [ "$iss_version" = "$version" ]; then
	ok ".iss MyAppVersion = $version"
else
	err ".iss MyAppVersion = '$iss_version', esperado '$version'"
fi
if grep -qF '#define MyAppName "remoteDesktop Host"' "$iss"; then
	ok ".iss MyAppName = remoteDesktop Host"
else
	err ".iss MyAppName diferente de 'remoteDesktop Host'"
fi
if grep -q 'PrivilegesRequired=lowest' "$iss"; then
	ok ".iss PrivilegesRequired=lowest (instalacao por usuario, sem administracao)"
else
	err ".iss sem PrivilegesRequired=lowest"
fi
if grep -qF 'DefaultDirName={localappdata}\Programs\remoteDesktop' "$iss"; then
	ok ".iss instala em Programs/remoteDesktop dentro do perfil do usuario (localappdata)"
else
	err ".iss: DefaultDirName nao esta em {localappdata}"
fi
if grep -qF 'WindowProbe.exe' "$iss" && grep -qF 'OutputBaseFilename=remoteDesktop-host-{#MyAppVersion}-win-x64-setup' "$iss"; then
	ok ".iss: exe WindowProbe.exe e nome de saida remoteDesktop-host-<versao>-win-x64-setup"
else
	err ".iss: WindowProbe.exe ou OutputBaseFilename inconsistente"
fi
if grep -qF 'Source: "..\publish\win-x64\*"' "$iss"; then
	ok ".iss consome a pasta de saida do publish (publish/win-x64)"
else
	err ".iss nao consome a pasta de saida do publish (publish/win-x64)"
fi
if grep -Ev '^[[:space:]]*;' "$iss" | grep -Eiq 'firewall|netsh|\brunas\b'; then
	err ".iss contem diretriz com firewall/elevacao (proibido neste escopo)"
else
	ok ".iss sem diretriz de firewall, netsh ou elevacao (comentarios nao contam)"
fi
if grep -q '^\[Code\]' "$iss"; then
	err ".iss contem secao [Code] (escopo nao prevê codigo Pascal)"
else
	ok ".iss sem secao [Code]"
fi
if grep -q 'publish-win-x64.ps1' "$installer_ps1" && grep -q 'ISCC.exe' "$installer_ps1"; then
	ok "build-installer.ps1 chama publicacao e ISCC.exe"
else
	err "build-installer.ps1 sem publicacao ou ISCC.exe"
fi
for ps1 in "$publish_ps1" "$installer_ps1"; do
	if grep -Ev '^[[:space:]]*#' "$ps1" | grep -Eiq 'firewall|netsh|New-NetFirewallRule|New-Service|schtasks|Start-Process.*runas'; then
		err "$(basename "$ps1"): contem comando de firewall/servico/tarefa (proibido neste escopo)"
	else
		ok "$(basename "$ps1"): sem firewall, servico, tarefa agendada ou elevacao"
	fi
done

# --- Documento ---------------------------------------------------------------
if grep -q "$version" "$doc"; then
	ok "docs/INSTALLERS.md menciona a versao $version"
else
	err "docs/INSTALLERS.md nao menciona a versao $version"
fi
if grep -q 'RemoteDesktop.app' "$doc" && grep -q 'WindowProbe.exe' "$doc"; then
	ok "docs/INSTALLERS.md documenta RemoteDesktop.app e WindowProbe.exe"
else
	err "docs/INSTALLERS.md sem RemoteDesktop.app ou WindowProbe.exe"
fi

if [ "$fail_count" -gt 0 ]; then
	echo "falhas: $fail_count" >&2
	exit 1
fi
echo "consistencia de versoes/nomes aprovada (versao $version)."
