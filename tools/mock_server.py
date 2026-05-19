"""
Mock-сервер для тестирования USB-агента PrinterCollector.

Эмулирует будущие endpoints Django:
  POST /api/v1/inventory/usb-agents/register/  -> выдаёт фиктивный токен
  POST /api/v1/inventory/usb-readings/         -> принимает batch и логирует
  GET  /api/v1/inventory                       -> 200 OK (для "Тест соединения")

Запуск:
  python mock_server.py             # порт 8000, master key = "test-key"
  python mock_server.py 9000 mykey  # порт 9000, master key = "mykey"

В агенте:
  ApiEndpoint = http://localhost:8000/api/v1/inventory
  MasterKey   = test-key
"""
import json
import secrets
import sys
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
MASTER_KEY = sys.argv[2] if len(sys.argv) > 2 else "test-key"

REGISTERED = {}  # agent_id -> token


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass  # глушим стандартный лог, печатаем сами

    def _read_json(self):
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length else b"{}"
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return None

    def _send_json(self, status, payload):
        body = json.dumps(payload, ensure_ascii=False, indent=2).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _stamp(self):
        return datetime.now(timezone.utc).strftime("%H:%M:%S")

    def do_GET(self):
        print(f"[{self._stamp()}] GET {self.path}")
        self._send_json(200, {"status": "ok", "service": "mock"})

    def do_POST(self):
        print(f"[{self._stamp()}] POST {self.path}")
        data = self._read_json()
        if data is None:
            self._send_json(400, {"error": "invalid json"})
            return

        if self.path.rstrip("/").endswith("/usb-agents/register"):
            return self._register(data)
        if self.path.rstrip("/").endswith("/usb-readings"):
            return self._readings(data)

        self._send_json(404, {"error": "unknown endpoint", "path": self.path})

    def _register(self, data):
        key = data.get("registration_key")
        agent_id = data.get("agent_id") or "unknown"
        hostname = data.get("hostname") or ""

        if key != MASTER_KEY:
            print(f"  registration: REJECT — wrong master key (got {key!r})")
            self._send_json(403, {"error": "Invalid registration key"})
            return

        token = REGISTERED.get(agent_id)
        if not token:
            token = secrets.token_hex(32)
            REGISTERED[agent_id] = token
            print(f"  registration: NEW agent_id={agent_id} hostname={hostname} -> token={token[:12]}…")
        else:
            print(f"  registration: existing agent_id={agent_id} -> token={token[:12]}…")

        self._send_json(201, {
            "agent_id": agent_id,
            "token": token,
            "message": "Agent registered (mock)"
        })

    def _readings(self, data):
        auth = self.headers.get("Authorization", "")
        if not auth.startswith("Bearer "):
            print("  readings: REJECT — no bearer token")
            self._send_json(401, {"error": "missing bearer token"})
            return
        token = auth[7:]
        if token not in REGISTERED.values():
            print(f"  readings: REJECT — unknown token {token[:12]}…")
            self._send_json(401, {"error": "unknown token"})
            return

        agent_id = data.get("agent_id")
        readings = data.get("readings", [])
        print(f"  readings: agent_id={agent_id} version={data.get('agent_version')} count={len(readings)}")
        results = []
        for r in readings:
            sn = (r.get("serial_number") or {}).get("value")
            counters = r.get("counters") or {}
            print(f"    - {r.get('timestamp')} S/N={sn} counters={counters} model={r.get('model')!r}")
            results.append({"serial_number": sn, "status": "success", "task_id": secrets.randbits(31)})

        self._send_json(200, {"processed": len(results), "results": results})


def main():
    server = HTTPServer(("0.0.0.0", PORT), Handler)
    print(f"Mock server listening on http://localhost:{PORT}")
    print(f"Master key: {MASTER_KEY!r}")
    print(f"Endpoints:  /api/v1/inventory/usb-agents/register/  /api/v1/inventory/usb-readings/")
    print("Ctrl+C to stop\n")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()
