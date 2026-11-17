"""A stand-in Xtream Codes server for testing the plugin without a real account.

Usage: mock_xtream.py <port> <ts-directory> <channel name> [more names...]
Serves player_api.php (server info, live streams, per-channel guide) and /timeshift/…,
which streams the .ts files of <ts-directory> in order (any real MPEG-TS will do).
Accepts only the test login below and prints every request, so a test can check what was asked.
"""
import base64, glob, json, os, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

PORT, TS_DIR, NAMES = int(sys.argv[1]), sys.argv[2], sys.argv[3:]
USER, PASS = "mockuser", "mockpass"
TZ_OFFSET = 3600  # the mock "provider" lives in UTC+1


def b64(s):
    return base64.b64encode(s.encode()).decode()


def guide(stream_id):
    now = int(time.time())
    start = now - now % 3600 - 30 * 3600
    out = []
    for i in range(34):
        s, e = start + i * 3600, start + (i + 1) * 3600
        out.append({
            "id": str(i), "title": b64(f"Mock programme {i} (stream {stream_id})"),
            "description": b64("Test programme from the mock Xtream server."),
            "start": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime(s + TZ_OFFSET)),
            "end": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime(e + TZ_OFFSET)),
            "start_timestamp": str(s), "stop_timestamp": str(e), "has_archive": 1,
        })
    return {"epg_listings": out}


class H(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        u = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(u.query).items()}
        print(time.strftime("%H:%M:%S"), "REQ", u.path, {k: ("***" if k == "password" else v) for k, v in q.items()}, flush=True)
        if u.path == "/player_api.php":
            if q.get("username") != USER or q.get("password") != PASS:
                return self.send_json({"user_info": {"auth": 0}}, 401)
            action = q.get("action", "")
            if action == "":
                now = int(time.time())
                return self.send_json({"user_info": {"auth": 1, "max_connections": "1"}, "server_info": {
                    "timezone": "Mock/Nowhere", "timestamp_now": now,
                    "time_now": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime(now + TZ_OFFSET))}})
            if action == "get_live_streams":
                return self.send_json([{"num": 900 + i, "name": n, "stream_id": 5000 + i, "tv_archive": 1 if i % 2 == 0 else "0",
                                        "tv_archive_duration": "3" if i % 2 == 0 else 0} for i, n in enumerate(NAMES)])
            if action == "get_simple_data_table":
                return self.send_json(guide(q.get("stream_id")))
            return self.send_json([])
        parts = u.path.strip("/").split("/")
        if len(parts) == 6 and parts[0] == "timeshift":
            _, user, pw, minutes, start, name = parts
            if user != USER or pw != PASS:
                self.send_response(403); self.end_headers(); return
            print(time.strftime("%H:%M:%S"), "TIMESHIFT", minutes, "min from", start, name, flush=True)
            self.send_response(200)
            self.send_header("Content-Type", "video/mp2t")
            self.end_headers()
            try:
                for f in sorted(glob.glob(os.path.join(TS_DIR, "seg_*.ts"))):
                    with open(f, "rb") as fh:
                        self.wfile.write(fh.read())
            except (BrokenPipeError, ConnectionResetError):
                print(time.strftime("%H:%M:%S"), "client closed", flush=True)
            return
        self.send_response(404); self.end_headers()

    def send_json(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


ThreadingHTTPServer(("0.0.0.0", PORT), H).serve_forever()
