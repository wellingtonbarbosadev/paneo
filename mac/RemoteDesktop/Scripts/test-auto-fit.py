#!/usr/bin/env python3
"""Integração auto-fit Mac real contra uma fixture TLS local, sem Keychain/PC."""
import base64
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import socket
import ssl
import struct
import subprocess
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[3]
MAC = ROOT / "mac/RemoteDesktop"
PIN = "246810"
ITERATIONS = 600_000
WINDOW_ID = 4242


def receive(stream):
    def exact(size):
        data = bytearray()
        while len(data) < size:
            chunk = stream.recv(size - len(data))
            if not chunk:
                raise EOFError()
            data.extend(chunk)
        return bytes(data)

    length, = struct.unpack("!I", exact(4))
    if not 0 < length <= 16 * 1024 * 1024:
        raise ValueError("invalid frame length")
    body = exact(length)
    if body[0] != 1:
        raise ValueError("unexpected frame kind")
    return json.loads(body[1:])


def send(stream, value):
    body = b"\x01" + json.dumps(value, separators=(",", ":")).encode()
    stream.sendall(struct.pack("!I", len(body)) + body)


def proof(salt, nonce):
    key = hashlib.pbkdf2_hmac("sha256", PIN.encode(), salt, ITERATIONS, 32)
    return hmac.new(key, b"remoteDesktop-v2/pin\x00" + nonce, hashlib.sha256).digest()


def main():
    build = Path(tempfile.mkdtemp(prefix="paneo-autofit-build-"))
    try:
        sources = sorted((MAC / "Sources/RemoteDesktop").glob("*.swift"))
        sources = [str(path) for path in sources if path.name not in ("RemoteDesktopApp.swift", "AppMenus.swift")]
        harness = MAC / "Scripts/AutoFitHarness.swift"
        binary = build / "autofit-harness"
        subprocess.run(["swiftc", "-swift-version", "6", "-suppress-warnings", "-parse-as-library",
                        *sources, str(harness), "-o", str(binary)], check=True)

        with tempfile.TemporaryDirectory(prefix="paneo-autofit-fixture-") as tmp:
            folder = Path(tmp)
            key, cert = folder / "fixture-key.pem", folder / "fixture-cert.pem"
            subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                            "-subj", "/CN=PaneoAutoFitFixture", "-keyout", str(key), "-out", str(cert)],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            os.chmod(key, 0o600)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(cert, key)
            listener = socket.socket()
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(("127.0.0.1", 0))
            listener.listen(8)
            listener.settimeout(0.25)
            port = listener.getsockname()[1]
            session, result = folder / "session.json", folder / "result.json"
            session.write_text(json.dumps({"host": "127.0.0.1", "port": str(port), "pin": PIN}))
            os.chmod(session, 0o600)
            commands = []
            views = []
            lock = threading.Lock()

            def serve_client(raw):
                try:
                    raw.settimeout(10)
                    with context.wrap_socket(raw, server_side=True) as stream:
                        hello = receive(stream)
                        if hello.get("type") != "hello" or hello.get("version") != 2:
                            raise ValueError("invalid hello")
                        salt, nonce = secrets.token_bytes(32), secrets.token_bytes(32)
                        send(stream, {"type": "challenge", "version": 2,
                                      "salt": base64.b64encode(salt).decode(),
                                      "nonce": base64.b64encode(nonce).decode()})
                        auth = receive(stream)
                        submitted = base64.b64decode(auth.get("proof", ""))
                        if auth.get("mode") != "pin" or not hmac.compare_digest(submitted, proof(salt, nonce)):
                            return
                        role = hello.get("role")
                        if role == "view":
                            with lock:
                                views.append({"monitorId": hello.get("monitorId"), "windowId": hello.get("windowId")})
                        send(stream, {"type": "ready", "version": 2,
                                      "role": role, "host": "LOCAL FIXTURE"})
                        if role == "control":
                            send(stream, {"type": "windows", "windows": [
                                {"id": WINDOW_ID, "title": "Fixture", "process": "fixture.exe",
                                 "width": 800, "height": 600, "minimized": False},
                                {"id": 4243, "title": "Second fixture", "process": "second.exe",
                                 "width": 640, "height": 480, "minimized": False},
                            ]})
                        elif role == "view":
                            if hello.get("windowId") in (WINDOW_ID, 4243, 4344) and hello.get("monitorId") is None:
                                send(stream, {"type": "windowState", "windowId": hello["windowId"],
                                              "minimized": False, "maximized": False, "reading": False,
                                              "canResize": True})
                        client_commands = []
                        while True:
                            message = receive(stream)
                            if message.get("type") == "ping":
                                send(stream, {"type": "pong"})
                                continue
                            if message.get("type") == "windowAction":
                                with lock:
                                    commands.append(dict(message, view={"windowId": hello.get("windowId"),
                                                                        "monitorId": hello.get("monitorId")}))
                                    client_commands.append(message)
                                    reading_count = sum(command.get("action") == "reading" and
                                                        command.get("enabled") is True for command in client_commands)
                                    update_fixed = (hello.get("windowId") == WINDOW_ID and
                                                    message.get("action") == "reading" and
                                                    message.get("enabled") is True and reading_count == 4)
                                    update_restored = (hello.get("windowId") == WINDOW_ID and
                                                       message.get("action") == "reading" and
                                                       message.get("enabled") is False)
                                    update_minimized_state = (hello.get("windowId") == WINDOW_ID and
                                                              message.get("action") in ("minimize", "restore"))
                                    detached_reading_enabled = sum(
                                        command.get("action") == "reading" and command.get("enabled") is True
                                        for command in client_commands
                                    )
                                    update_slider_window_state = (
                                        hello.get("windowId") == 4344 and
                                        message.get("action") == "reading" and
                                        message.get("enabled") is True and detached_reading_enabled == 4
                                    )
                                if update_fixed:
                                    send(stream, {"type": "windowState", "windowId": WINDOW_ID,
                                                  "minimized": False, "maximized": False, "reading": False,
                                                  "canResize": False})
                                if update_restored:
                                    send(stream, {"type": "windowState", "windowId": WINDOW_ID,
                                                  "minimized": False, "maximized": False, "reading": False,
                                                  "canResize": True})
                                if update_minimized_state:
                                    send(stream, {"type": "windowState", "windowId": WINDOW_ID,
                                                  "minimized": message.get("action") == "minimize",
                                                  "maximized": False, "reading": False, "canResize": True})
                                if update_slider_window_state:
                                    send(stream, {"type": "windowState", "windowId": 4344,
                                                  "minimized": False, "maximized": True,
                                                  "reading": False, "canResize": True})
                except (EOFError, OSError, ssl.SSLError, ValueError):
                    pass

            stopping = threading.Event()

            def accept_loop():
                while not stopping.is_set():
                    try:
                        raw, _ = listener.accept()
                    except socket.timeout:
                        continue
                    except OSError:
                        return
                    threading.Thread(target=serve_client, args=(raw,), daemon=True).start()

            server = threading.Thread(target=accept_loop, daemon=True)
            server.start()
            run = subprocess.run([str(binary), str(session), str(result)], timeout=60,
                                 capture_output=True, text=True)
            stopping.set()
            listener.close()
            server.join(timeout=1)
            report = json.loads(result.read_text()) if result.exists() else {}
            assert run.returncode == 0 and report.get("passed"), \
                f"Swift controller harness failed (exit={run.returncode}, report={report}, " \
                f"stdout={run.stdout.strip()!r}): {run.stderr.strip()}"
            print(run.stdout, end="")
            main_commands = [message for message in commands if message["view"].get("windowId") == WINDOW_ID]
            main_reading = [message for message in main_commands if message.get("action") == "reading"]
            main_enabled = [message for message in main_reading if message.get("enabled") is True]
            detached_reading = [message for message in commands if message["view"].get("windowId") == 4344 and
                                message.get("action") == "reading" and message.get("enabled") is True]
            expected_main = (report.get("initial"), report.get("resized"),
                             report.get("enabledAfterResize"), report.get("barChanged"), report.get("reEnabled"),
                             report.get("preferenceMain"))
            zoom_main = report.get("zoomMain", {})
            expected_zoom_main = zoom_main.get("commands", [])
            assert len(main_enabled) == len(expected_main) + len(expected_zoom_main) + 1, \
                f"main controller baseline/zoom reading count failed: {main_reading}"
            for message, bounds in zip(main_enabled[:len(expected_main)], expected_main):
                assert (message.get("width"), message.get("height")) == tuple(bounds), \
                    f"main command did not match video bounds {bounds}: {message}"
            main_zoom_messages = main_enabled[len(expected_main):len(expected_main) + len(expected_zoom_main)]
            for message, bounds in zip(main_zoom_messages, expected_zoom_main):
                assert (message.get("width"), message.get("height")) == tuple(bounds), \
                    f"main zoom command did not use inverse scale {bounds}: {message}"
            assert tuple(main_enabled[-1].get(k) for k in ("width", "height")) == \
                   tuple(zoom_main.get("resizeBounds")), \
                "restore should return to the current 1x window bounds"
            assert len([message for message in main_reading if message.get("enabled") is False]) == 4, \
                f"opt-out, fixed-state cleanup and explicit maximize should disable auto-fit: {main_reading}"
            zoom_detached = report.get("zoomDetached", {})
            expected_zoom_detached = zoom_detached.get("commands", [])
            assert (len(detached_reading) == 3 + len(expected_zoom_detached) and
                    tuple(detached_reading[0].get(k) for k in ("width", "height")) == tuple(report["detachedInitial"]) and
                    tuple(detached_reading[1].get(k) for k in ("width", "height")) == tuple(report["detachedResized"]) and
                    tuple(detached_reading[2].get(k) for k in ("width", "height")) == tuple(report["preferenceDetached"]) and
                    all(tuple(message.get(k) for k in ("width", "height")) == tuple(bounds)
                        for message, bounds in zip(detached_reading[3:3 + len(expected_zoom_detached)],
                                                   expected_zoom_detached))), \
                f"detached controller auto-fit commands mismatch: {detached_reading}"
            assert sum(message["view"].get("windowId") == 4344 and message.get("action") == "reading" and
                       message.get("enabled") is False for message in commands) == 1, \
                "preference opt-out should disable detached auto-fit once"
            slider_batch = report.get("sliderBatch", {})
            assert slider_batch.get("moves") == 3 and slider_batch.get("scale") == 1.1 and \
                   slider_batch.get("percentage") == "110%" and \
                   slider_batch.get("focusSurvivedState") is True, \
                f"slider final value/focus failed: {slider_batch}"
            assert any(message.get("action") == "maximize" for message in main_commands), "explicit maximize was not sent"
            maximize_index = main_commands.index(next(m for m in main_commands if m.get("action") == "maximize"))
            first_zoom = main_commands.index(main_zoom_messages[0])
            assert not any(message.get("action") == "reading" and message.get("enabled") is True
                           for message in main_commands[maximize_index + 1:first_zoom]), \
                "auto-fit ran after maximize before an explicit zoom"
            minimize_index = main_commands.index(next(m for m in main_commands if m.get("action") == "minimize"))
            restore_index = main_commands.index(next(m for m in main_commands if m.get("action") == "restore"),
                                                minimize_index + 1)
            assert not any(message.get("action") == "reading" and message.get("enabled") is True
                           for message in main_commands[minimize_index + 1:restore_index]), \
                "minimized window received an auto-fit command"
            assert report.get("zoomSelectionReset", {}).get("windowId") == 4243 and \
                   report.get("zoomSelectionReset", {}).get("scale") == 1, \
                "selecting another window resets its zoom scale"
            assert any(view.get("monitorId") == 17 for view in views), f"monitor view hello missing: {views}"
            assert any(view.get("windowId") == 4343 for view in views), f"legacy view connection missing: {views}"
            assert not any(message["view"].get("monitorId") == 17 and message.get("action") == "reading"
                           for message in commands), f"monitor received auto-fit: {commands}"
            assert not any(message["view"].get("windowId") == 4343 and message.get("action") == "reading"
                           for message in commands), f"legacy host received auto-fit: {commands}"
            print(json.dumps({"passed": True, "commands": commands, "viewHellos": views,
                              "initialBounds": report.get("initial"), "resizedBounds": report.get("resized"),
                              "barBounds": report.get("barChanged"), "fixedBounds": report.get("fixedBounds"),
                              "detachedBounds": [report.get("detachedInitial"), report.get("detachedResized")],
                              "zoomMain": zoom_main, "zoomDetached": zoom_detached,
                              "sliderBatch": slider_batch,
                              "beforeMaximize": report.get("beforeMaximize"),
                              "scope": report.get("scope")}))
    finally:
        import shutil
        shutil.rmtree(build, ignore_errors=True)


if __name__ == "__main__":
    main()
