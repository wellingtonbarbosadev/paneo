#!/usr/bin/env python3
"""TLS v2 window-command fixture plus short proof through the real AppKit client."""
import argparse
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import subprocess
import tempfile
import time
import unicodedata
import zipfile

ROOT = Path(__file__).resolve().parent.parent
KEY = ROOT / '.tools/ssh/remoteDesktop_ed25519'
ARTIFACTS = ROOT / 'artifacts'


def scp(arguments, quiet=False):
    return subprocess.run(['scp', '-i', str(KEY), '-o', 'IdentitiesOnly=yes',
                           '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10',
                           *arguments], capture_output=quiet, timeout=30)


def receive_exact(stream, count):
    data = bytearray()
    while len(data) < count:
        part = stream.recv(count - len(data))
        if not part:
            raise ConnectionError('Host encerrou a sessão TLS.')
        data.extend(part)
    return bytes(data)


def receive(stream):
    size, = struct.unpack('!I', receive_exact(stream, 4))
    if not 0 < size <= 16 * 1024 * 1024:
        raise ValueError('Tamanho de frame inválido.')
    body = receive_exact(stream, size)
    if body[0] == 1:
        return 'control', json.loads(body[1:])
    if body[0] == 2:
        return 'media', None
    raise ValueError('Tipo de frame desconhecido.')


def send(stream, message):
    body = b'\x01' + json.dumps(message, separators=(',', ':')).encode()
    stream.sendall(struct.pack('!I', len(body)) + body)


def pin_proof(pin, salt, nonce):
    normalized = unicodedata.normalize('NFKC', pin.strip())
    key = hashlib.pbkdf2_hmac('sha256', normalized.encode(), salt, 600_000, 32)
    return hmac.new(key, b'remoteDesktop-v2/pin\x00' + nonce, hashlib.sha256).digest()


def connect(config):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    tcp = socket.create_connection((config['host'], config['port']), timeout=20)
    stream = context.wrap_socket(tcp, server_hostname='Paneo')
    cert = stream.getpeercert(binary_form=True)
    if hashlib.sha256(cert).hexdigest().upper() != config['fingerprint'].replace(':', '').upper():
        stream.close()
        raise ValueError('Fingerprint TLS divergente.')
    stream.settimeout(15)
    send(stream, {'type': 'hello', 'version': 2, 'role': 'view', 'clientName': 'WindowCommandsHarness',
                  'windowId': config['windowId'], 'windowCommands': True})
    kind, challenge = receive(stream)
    if kind != 'control' or challenge.get('type') != 'challenge' or challenge.get('version') != 2:
        stream.close()
        raise ValueError('Handshake v2 recusado antes do challenge.')
    salt, nonce = base64.b64decode(challenge['salt']), base64.b64decode(challenge['nonce'])
    send(stream, {'type': 'auth', 'version': 2, 'mode': 'pin',
                  'proof': base64.b64encode(pin_proof(config['pin'], salt, nonce)).decode()})
    kind, ready = receive(stream)
    if kind != 'control' or ready.get('type') != 'ready' or ready.get('role') != 'view':
        stream.close()
        raise ValueError('Autenticação TLS v2 recusada.')
    return stream


def wait_for(stream, message_type, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        kind, message = receive(stream)
        if kind == 'control':
            if message.get('type') == 'error':
                raise RuntimeError(f"Host reportou erro {message.get('code', 'protocol')} esperando {message_type}.")
            if message.get('type') == message_type:
                return message
    raise TimeoutError(f'Host não respondeu com {message_type}.')


def action(stream, name, expected_state=None, **fields):
    send(stream, {'type': 'windowAction', 'action': name, **fields})
    deadline = time.monotonic() + 15
    result, state = None, None
    while time.monotonic() < deadline:
        kind, message = receive(stream)
        if kind != 'control':
            continue
        if message.get('type') == 'windowCommandResult' and message.get('action') == name:
            result = message
            if not result.get('success'):
                raise AssertionError(f'Ação {name} recusada: {result.get("message", "sem detalhe")}')
        elif message.get('type') == 'windowState' and expected_state is not None:
            if message.get(expected_state[0]) == expected_state[1]:
                state = message
        if result is not None and (expected_state is None or state is not None):
            return result, state
    raise TimeoutError(f'Host não confirmou ação/estado {name}.')


def invoke_menu(stream, item_id, expected_success):
    send(stream, {'type': 'windowMenuInvoke', 'id': item_id})
    result = wait_for(stream, 'windowCommandResult')
    if result.get('action') != 'menu' or bool(result.get('success')) != expected_success:
        raise AssertionError('Resultado de invocação de menu divergiu do esperado.')
    return result


def flatten(items):
    for item in items:
        yield item
        yield from flatten(item.get('children') or [])


def test_commands(config, wait_close_dialog):
    stream = connect(config)
    evidence = {'authenticatedV2': True}
    drop_without_bye = False
    try:
        send(stream, {'type': 'windowMenus'})
        menus = wait_for(stream, 'windowMenus')
        if not menus.get('supported'):
            raise AssertionError('Menus indisponíveis: ' + str(menus.get('reason', 'sem motivo')))
        items = list(flatten(menus.get('items', [])))
        by_title = {item.get('title'): item for item in items}
        if not all(title in by_title for title in ('Run fixture command', 'Disabled fixture command',
                                                     'Checked fixture command', 'Nested fixture menu',
                                                     'Nested fixture command', 'Disabled fixture submenu',
                                                     'Child of disabled fixture menu', 'Dynamic parent fixture submenu',
                                                     'Child to disable after snapshot', 'Disable dynamic parent')):
            shape = [(item.get('title'), item.get('enabled'), item.get('checked'), item.get('separator'),
                      len(item.get('children') or [])) for item in items]
            raise AssertionError(f'Árvore Win32 fixture incompleta; itens/flags observados: {shape}')
        if by_title['Disabled fixture command'].get('enabled') or not by_title['Checked fixture command'].get('checked'):
            raise AssertionError('Estados disabled/checked do menu foram perdidos.')
        if not any(item.get('separator') for item in items) or not by_title['Nested fixture command'].get('id'):
            raise AssertionError('Separador ou submenu não foi preservado.')

        old_enabled_id = by_title['Run fixture command']['id']
        invoke_menu(stream, by_title['Disabled fixture command']['id'], False)
        if by_title['Child of disabled fixture menu'].get('enabled'):
            raise AssertionError('Comando filho de submenu desabilitado apareceu habilitado.')
        invoke_menu(stream, by_title['Child of disabled fixture menu']['id'], False)
        dynamic_child = by_title['Child to disable after snapshot']
        if not dynamic_child.get('enabled'):
            raise AssertionError('Comando filho da fixture dinâmica iniciou desabilitado.')
        invoke_menu(stream, by_title['Disable dynamic parent']['id'], True)
        time.sleep(0.2)  # WM_COMMAND é entregue ao thread do HWND por PostMessage.
        invoke_menu(stream, dynamic_child['id'], False)
        invoke_menu(stream, 'arbitrary-stale-menu-id', False)
        send(stream, {'type': 'windowMenus'})
        refreshed = wait_for(stream, 'windowMenus')
        refreshed_items = {item.get('title'): item for item in flatten(refreshed.get('items', []))}
        if not refreshed.get('supported') or not refreshed_items.get('Run fixture command', {}).get('id'):
            raise AssertionError('Atualização de menu não gerou novo identificador válido.')
        if refreshed_items['Child to disable after snapshot'].get('enabled'):
            raise AssertionError('Releitura do menu não propagou estado do pai desabilitado.')
        invoke_menu(stream, old_enabled_id, False)
        invoke_menu(stream, refreshed_items['Run fixture command']['id'], True)
        evidence['menus'] = {'supported': True, 'items': len(items), 'disabled': True, 'checked': True, 'submenu': True}

        action(stream, 'minimize', ('minimized', True))
        action(stream, 'restore', ('minimized', False))
        action(stream, 'maximize', ('maximized', True))
        action(stream, 'reading', ('reading', True), enabled=True, width=960, height=600)
        action(stream, 'reading', ('reading', False), enabled=False)
        _, restored_state = action(stream, 'state', ('maximized', True))
        if restored_state.get('reading'):
            raise AssertionError('Modo leitura permaneceu ativo após restaurar geometria maximizada.')
        action(stream, 'restore', ('maximized', False))
        action(stream, 'close')
        wait_close_dialog()
        send(stream, {'type': 'key', 'key': 'escape'})
        send(stream, {'type': 'ping'})
        wait_for(stream, 'pong')
        evidence['actions'] = ['minimize', 'restore', 'maximize', 'reading-on', 'reading-off-restores-maximized-state',
                               'restore', 'close-cancelled-by-escape']

        action(stream, 'reading', ('reading', True), enabled=True, width=960, height=600)
        evidence['abruptDisconnectWithReadingLease'] = True
        drop_without_bye = True
        return evidence
    finally:
        if not drop_without_bye:
            try:
                send(stream, {'type': 'bye'})
            except OSError:
                pass
        else:
            stream.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack('ii', 1, 0))
        stream.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', default='100.105.238.91', help='IP Tailscale do PC Windows')
    parser.add_argument('--binary', type=Path, default=ROOT / 'packaging/mac/dist/Paneo.app/Contents/MacOS/RemoteDesktop',
                        help='executável de bundle AppKit para prova curta de vídeo')
    args = parser.parse_args()
    if not args.binary.is_file():
        parser.error('Compile o bundle Mac antes de executar a prova.')
    if not KEY.is_file():
        parser.error('A chave SSH de desenvolvimento existente não foi encontrada.')

    ARTIFACTS.mkdir(exist_ok=True)
    archive = ARTIFACTS / 'window-commands-source.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as output:
        for file in (ROOT / 'windows/WindowProbe').rglob('*'):
            relative = file.relative_to(ROOT)
            if file.is_file() and not {'bin', 'obj'}.intersection(relative.parts):
                output.write(file, relative)
        icon = ROOT / 'assets/branding/paneo.ico'
        if icon.is_file():
            output.write(icon, icon.relative_to(ROOT))
        version = ROOT / 'packaging/VERSION'
        if version.is_file():
            output.write(version, version.relative_to(ROOT))
    remote = f'welli@{args.host}:remoteDesktop-dev/'
    scp([str(archive), remote + 'window-commands-source.zip']).check_returncode()

    result = {'success': False, 'host': args.host}
    log = (ARTIFACTS / 'window-commands-host.log').open('w')
    runner = subprocess.Popen(['python3', str(ROOT / 'scripts/windows-ssh.py'),
                               str(ROOT / 'scripts/Run-WindowCommandsValidation.ps1'), '--host', args.host],
                              stdout=log, stderr=subprocess.STDOUT)
    with tempfile.TemporaryDirectory(prefix='paneo-window-commands-') as temporary:
        private = Path(temporary)
        session_path, client_output = private / 'session.json', private / 'mac-client-result.json'
        try:
            for _ in range(60):
                if runner.poll() is not None:
                    raise RuntimeError('Host fixture encerrou antes de disponibilizar a sessão.')
                if scp([remote + 'artifacts/window-commands-validation/session.json', str(session_path)], quiet=True).returncode == 0:
                    break
                time.sleep(1)
            else:
                raise TimeoutError('Host não disponibilizou a fixture em 60 s.')
            os.chmod(session_path, 0o600)
            config = json.loads(session_path.read_text())
            config.update(host=args.host)
            def wait_close_dialog():
                marker = private / 'close-dialog-ready'
                deadline = time.monotonic() + 15
                marker_remote = remote + 'artifacts/window-commands-validation/close-dialog-ready'
                while time.monotonic() < deadline:
                    if scp([marker_remote, str(marker)], quiet=True).returncode == 0:
                        return
                    time.sleep(0.2)
                raise TimeoutError('Fixture não confirmou que o diálogo de fechamento estava visível.')

            result['windows'] = test_commands(config, wait_close_dialog)
            client_session = dict(config, durationSeconds=10, allowLongDuration=False, maxDimension=1280)
            session_path.write_text(json.dumps(client_session))
            os.chmod(session_path, 0o600)
            config.clear(); client_session.clear()
            smoke = [str(args.binary), '--remote-smoke', str(session_path), str(client_output)]
            if Path('/usr/bin/caffeinate').is_file():
                smoke = ['/usr/bin/caffeinate', '-di', *smoke]
            client = subprocess.run(smoke,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=45)
            result['macClientExitCode'] = client.returncode
            result['macClient'] = json.loads(client_output.read_text()) if client_output.exists() else {}
            done = private / 'client-done'
            done.write_text('passed' if client.returncode == 0 and result['macClient'].get('success') else 'failed')
            os.chmod(done, 0o600)
            scp([str(done), remote + 'artifacts/window-commands-validation/client-done'], quiet=True).check_returncode()
            try:
                runner.wait(timeout=45)
            except subprocess.TimeoutExpired:
                result['hostCleanup'] = 'Tarefa remota permanece limitada ao timeout temporário de 9 min.'
            report = private / 'host-result.json'
            if scp([remote + 'artifacts/window-commands-validation/host-result.json', str(report)], quiet=True).returncode == 0:
                result['hostFixture'] = json.loads(report.read_text())
            result['success'] = bool(result['windows'] and result['macClient'].get('success') and
                                     result.get('hostFixture', {}).get('success'))
        except Exception as error:
            result.update(error=type(error).__name__, detail=str(error))
        finally:
            done = private / 'client-done'
            if not done.exists():
                done.write_text('failed'); os.chmod(done, 0o600)
                scp([str(done), remote + 'artifacts/window-commands-validation/client-done'], quiet=True)
            if runner.poll() is None:
                try:
                    runner.wait(timeout=45)
                except subprocess.TimeoutExpired:
                    runner.terminate()
    log.close()
    (ARTIFACTS / 'window-commands-validation.json').write_text(json.dumps(result, indent=2))
    print(json.dumps(result, indent=2))
    return 0 if result['success'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
