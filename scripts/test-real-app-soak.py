#!/usr/bin/env python3
"""Soak AppKit real de 30 min contra uma instância Notepad descartável no Windows.

O host usa RealAppSoak (TLS/protocolo v2). O cliente é o executável de um bundle
Paneo atual, abre uma NSWindow/AVSampleBufferDisplayLayer real e não envia input.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / 'artifacts'
REMOTE = 'welli@{host}:remoteDesktop-dev/'
KEY = ROOT / '.tools/ssh/remoteDesktop_ed25519'


def copy(args, quiet=False):
    return subprocess.run(['scp', '-i', str(KEY), '-o', 'IdentitiesOnly=yes',
                           '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10',
                           *args], capture_output=quiet, timeout=30)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', default='100.105.238.91', help='IP Tailscale do PC Windows')
    parser.add_argument('--duration', type=int, default=1800, help='segundos de medição AppKit (1–1800; somente 1800 conclui o soak pedido)')
    parser.add_argument('--binary', type=Path, default=ROOT / 'packaging/mac/dist/Paneo.app/Contents/MacOS/RemoteDesktop',
                        help='executável do bundle AppKit já compilado')
    args = parser.parse_args()
    if not 1 <= args.duration <= 1800:
        parser.error('--duration deve estar entre 1 e 1800 segundos')
    if not args.binary.is_file():
        parser.error('Executável AppKit não encontrado; compile o bundle antes do soak.')
    if not KEY.is_file():
        parser.error('A chave SSH de desenvolvimento existente não foi encontrada.')

    ARTIFACTS.mkdir(exist_ok=True)
    archive = ARTIFACTS / 'soak-source.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
        for file in (ROOT / 'windows/WindowProbe').rglob('*'):
            if file.is_file() and not {'bin', 'obj'}.intersection(file.relative_to(ROOT).parts):
                output.write(file, file.relative_to(ROOT))
        icon = ROOT / 'assets/branding/paneo.ico'
        if icon.is_file():
            output.write(icon, icon.relative_to(ROOT))
        version = ROOT / 'packaging/VERSION'
        if version.is_file():
            output.write(version, version.relative_to(ROOT))
    target = REMOTE.format(host=args.host)
    copy([str(archive), target + 'soak-source.zip']).check_returncode()

    result = {'success': False, 'application': 'Notepad descartável', 'client': 'bundle AppKit real',
              'scope': 'soak de 30 min' if args.duration == 1800 else 'preflight curto; não atende ao soak de 30 min',
              'requestedDurationSeconds': args.duration}
    host_log = ARTIFACTS / 'real-app-soak-host.log'
    with tempfile.TemporaryDirectory(prefix='paneo-appkit-soak-') as temporary:
        private = Path(temporary)
        session = private / 'session.json'
        output_path = ARTIFACTS / 'real-app-soak-client-live.json'
        progress_path = Path(str(output_path) + '.progress.json')
        output_path.unlink(missing_ok=True)
        progress_path.unlink(missing_ok=True)
        runner = subprocess.Popen(['python3', str(ROOT / 'scripts/windows-ssh.py'),
                                   str(ROOT / 'scripts/Run-RealAppSoak.ps1'), '--host', args.host],
                                  stdout=host_log.open('w'), stderr=subprocess.STDOUT)
        client = None
        try:
            for _ in range(90):
                if runner.poll() is not None:
                    raise RuntimeError('Host terminou antes de disponibilizar a sessão; consulte o log sem credenciais.')
                if copy([target + 'artifacts/real-app-soak/session.json', str(session)], quiet=True).returncode == 0:
                    break
                time.sleep(1)
            else:
                raise TimeoutError('Host não disponibilizou sessão em 90 s.')
            os.chmod(session, 0o600)
            configuration = json.loads(session.read_text())
            configuration.update(host=args.host, durationSeconds=args.duration,
                                 allowLongDuration=args.duration > 60, maxDimension=1280)
            session.write_text(json.dumps(configuration))
            os.chmod(session, 0o600)
            configuration.clear()
            previous_mask = os.umask(0o077)
            try:
                smoke = [str(args.binary), '--remote-smoke', str(session), str(output_path)]
                if Path('/usr/bin/caffeinate').is_file():
                    smoke = ['/usr/bin/caffeinate', '-di', *smoke]
                client = subprocess.Popen(smoke,
                                          stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            finally:
                os.umask(previous_mask)
            deadline = time.monotonic() + args.duration + 90
            last_checkpoint = 0
            while client.poll() is None and time.monotonic() < deadline:
                if progress_path.exists():
                    checkpoint = json.loads(progress_path.read_text())
                    if checkpoint.get('elapsedSeconds', 0) > last_checkpoint:
                        last_checkpoint = checkpoint['elapsedSeconds']
                        print(json.dumps({key: checkpoint.get(key) for key in
                                          ('elapsedSeconds', 'decodedFrames', 'displayEnqueuedFrames',
                                           'displayEnqueueRejected', 'maxMediaGapSeconds', 'errors')}), flush=True)
                time.sleep(1)
            if client.poll() is None:
                raise subprocess.TimeoutExpired([str(args.binary), '--remote-smoke'], args.duration + 90)
            exit_code = client.returncode
            report = json.loads(output_path.read_text()) if output_path.exists() else {}
            result.update(report, clientExitCode=exit_code)
            done = private / 'client-done'
            done.write_text('passed' if exit_code == 0 and report.get('success') else 'failed')
            os.chmod(done, 0o600)
            copy([str(done), target + 'artifacts/real-app-soak/client-done'], quiet=True).check_returncode()
            try:
                runner.wait(timeout=45)
            except subprocess.TimeoutExpired:
                result['hostCleanup'] = 'O processo remoto permanece limitado ao timeout de 34 min da tarefa temporária.'
            host_report = private / 'host-result.json'
            if copy([target + 'artifacts/real-app-soak/host-result.json', str(host_report)], quiet=True).returncode == 0:
                result['host'] = json.loads(host_report.read_text())
            result['success'] = bool(exit_code == 0 and result.get('measuredSeconds', 0) >= args.duration
                                     and result.get('host', {}).get('success'))
        except Exception as error:
            result.update(error=type(error).__name__, detail=str(error))
        finally:
            if client is not None and client.poll() is None:
                client.terminate()
                try:
                    client.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    client.kill()
            if not (private / 'client-done').exists():
                done = private / 'client-done'
                done.write_text('failed')
                os.chmod(done, 0o600)
                copy([str(done), target + 'artifacts/real-app-soak/client-done'], quiet=True)
            if runner.poll() is None:
                try:
                    runner.wait(timeout=45)
                except subprocess.TimeoutExpired:
                    runner.terminate()
            copy([target + 'artifacts/real-app-soak/host-result.json', str(ARTIFACTS / 'real-app-soak-host.json')], quiet=True)
            (ARTIFACTS / 'real-app-soak-client.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))
    return 0 if result['success'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
