#!/usr/bin/env python3
"""Build e execução interativa opt-in da matriz Windows de aplicativos reais.

Abre instâncias e documentos descartáveis apenas quando consegue identificá-los
por HWND novo. O relatório contém versões e cenários, sem títulos ou conteúdo.
"""
import argparse
import json
import subprocess
import tempfile
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
KEY = ROOT / '.tools/ssh/remoteDesktop_ed25519'
REMOTE_ROOT = 'welli@{host}:remoteDesktop-dev/'


def scp(args, quiet=False):
    return subprocess.run(['scp', '-i', str(KEY), '-o', 'IdentitiesOnly=yes',
                           '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10',
                           *args], capture_output=quiet, timeout=30)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', default='100.105.238.91', help='IP Tailscale do PC Windows')
    args = parser.parse_args()
    if not KEY.is_file():
        parser.error('A chave SSH de desenvolvimento existente não foi encontrada.')

    artifacts = ROOT / 'artifacts'
    artifacts.mkdir(exist_ok=True)
    archive = artifacts / 'application-compatibility-source.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
        for file in (ROOT / 'windows/WindowProbe').rglob('*'):
            if file.is_file() and not {'bin', 'obj'}.intersection(file.relative_to(ROOT).parts):
                output.write(file, file.relative_to(ROOT))
        for path in (ROOT / 'assets/branding/paneo.ico', ROOT / 'packaging/VERSION'):
            if path.is_file():
                output.write(path, path.relative_to(ROOT))

    target = REMOTE_ROOT.format(host=args.host)
    scp([str(archive), target + 'application-compatibility-source.zip']).check_returncode()
    log = artifacts / 'application-compatibility-host.log'
    with log.open('w') as destination:
        runner = subprocess.Popen(['python3', str(ROOT / 'scripts/windows-ssh.py'),
                                   str(ROOT / 'scripts/Run-ApplicationCompatibility.ps1'),
                                   '--host', args.host], stdout=destination, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 8 * 60
        while runner.poll() is None and time.monotonic() < deadline:
            time.sleep(1)
        if runner.poll() is None:
            runner.terminate()
            runner.wait(timeout=10)
            raise TimeoutError('Matriz excedeu oito minutos; não foi enviado comando para encerrar processos de aplicativo.')
    with tempfile.TemporaryDirectory(prefix='paneo-app-compat-report-') as temporary:
        report = Path(temporary) / 'application-compatibility-result.json'
        status = scp([target + 'artifacts/application-compatibility/application-compatibility-result.json',
                      str(report)], quiet=True)
        if status.returncode != 0:
            raise RuntimeError('Host terminou sem disponibilizar o relatório da matriz.')
        result = json.loads(report.read_text())
        destination_root = artifacts / 'application-compatibility'
        destination_root.mkdir(exist_ok=True)
        destination = destination_root / 'application-compatibility-result.json'
        destination.write_bytes(report.read_bytes())
        for app in result.get('apps', []):
            for capture in app.get('captures', []):
                relative = Path(capture['path'].replace('\\', '/'))
                local = destination_root / relative
                local.parent.mkdir(parents=True, exist_ok=True)
                remote_path = 'artifacts/application-compatibility/' + relative.as_posix()
                scp([target + remote_path, str(local)], quiet=True).check_returncode()
        print(destination)
        if runner.returncode != 0 or not result.get('success'):
            print('Matriz concluída com falhas; relatório e capturas foram preservados.')
            return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
