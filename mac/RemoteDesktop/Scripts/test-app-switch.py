#!/usr/bin/env python3
"""Compila e roda o repro AppKit de troca visual entre aplicativos."""
import json
import os
from pathlib import Path
import socket
import ssl
import subprocess
import tempfile
import threading

ROOT = Path(__file__).resolve().parents[3]
MAC = ROOT / "mac/RemoteDesktop"


def main():
    with tempfile.TemporaryDirectory(prefix="paneo-app-switch-") as tmp:
        folder = Path(tmp)
        sources = sorted((MAC / "Sources/RemoteDesktop").glob("*.swift"))
        sources = [str(path) for path in sources if path.name not in ("RemoteDesktopApp.swift", "AppMenus.swift")]
        harness = MAC / "Scripts/AppSwitchHarness.swift"
        binary = folder / "app-switch-harness"
        subprocess.run(["swiftc", "-swift-version", "6", "-suppress-warnings", "-parse-as-library",
                        *sources, str(harness), "-o", str(binary)], check=True)
        key, cert = folder / "fixture-key.pem", folder / "fixture-cert.pem"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-subj", "/CN=PaneoAppSwitchFixture", "-keyout", str(key), "-out", str(cert)],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        os.chmod(key, 0o600)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert, key)
        listener = socket.socket()
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("127.0.0.1", 0))
        listener.listen(8)
        listener.settimeout(0.1)
        session, result = folder / "session.json", folder / "result.json"
        session.write_text(json.dumps({"host": "127.0.0.1", "port": str(listener.getsockname()[1]), "pin": "246810"}))
        stopping = threading.Event()
        clients = []

        def serve(raw):
            try:
                with context.wrap_socket(raw, server_side=True) as stream:
                    stream.settimeout(0.1)
                    while not stopping.is_set():
                        try:
                            if not stream.recv(4096):
                                return
                        except socket.timeout:
                            continue
            except (OSError, ssl.SSLError):
                pass

        def accept():
            while not stopping.is_set():
                try:
                    raw, _ = listener.accept()
                except socket.timeout:
                    continue
                except OSError:
                    return
                client = threading.Thread(target=serve, args=(raw,), daemon=True)
                clients.append(client)
                client.start()

        server = threading.Thread(target=accept, daemon=True)
        server.start()
        try:
            run = subprocess.run([str(binary), str(session), str(result)], timeout=10, text=True, capture_output=True)
        finally:
            stopping.set()
            listener.close()
            server.join(timeout=1)
        print(run.stdout, end="")
        if run.returncode != 0:
            print(run.stderr, end="")
            if result.exists():
                print(json.dumps(json.loads(result.read_text()), sort_keys=True))
            raise SystemExit(run.returncode)


if __name__ == "__main__":
    main()
