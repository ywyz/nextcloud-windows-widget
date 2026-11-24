"""Create an isolated v0.1 profile and serve a loopback Chat Completions fixture."""
import json
import pathlib
import sqlite3
import sys
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

directory = pathlib.Path(sys.argv[1])
directory.mkdir(exist_ok=True)
if any(directory.iterdir()):
    raise ValueError("The UI fixture requires an empty directory")

account = uuid.uuid4()
cloud = "https://127.0.0.1:1/"
(directory / "account.json").write_text(
    json.dumps({"CacheId": str(account), "Server": cloud, "User": "ui-acceptance"}), encoding="utf-8"
)
with sqlite3.connect(directory / (account.hex + ".db")) as db:
    # The exact mail table shipped with v0.1.0 exercises cache migration.
    db.executescript("""
        CREATE TABLE lists(id TEXT PRIMARY KEY,name TEXT NOT NULL,writable INTEGER NOT NULL,token TEXT,present INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE mail(id TEXT PRIMARY KEY,subject TEXT NOT NULL,sender TEXT NOT NULL,date TEXT NOT NULL,body TEXT NOT NULL,unread INTEGER NOT NULL);
        CREATE TABLE sync(channel TEXT PRIMARY KEY,stamp TEXT NOT NULL);
    """)
    db.execute("INSERT INTO lists(id,name,writable) VALUES(?,?,1)", (cloud + "tasks/", "Personal"))
    for i in range(1, 4):
        db.execute("INSERT INTO mail VALUES(?,?,?,?,?,1)", (
            str(i), f"QA message {i}", "Sender <fixture@example.test>",
            f"2026-01-0{i}T10:15:00.0000000+00:00", f"Only this message body {i} should be translated."
        ))
    for channel in ["tasks", "mail"]:
        db.execute("INSERT INTO sync VALUES(?,?)", (channel, "2026-01-03T11:00:00.0000000+00:00"))


class Model(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        if self.path != "/v1/chat/completions":
            self.send_error(404)
            return
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            raw = bytearray()
            while True:
                size = int(self.rfile.readline(256).split(b";")[0].strip(), 16)
                if not size:
                    self.rfile.readline(256)
                    break
                if size < 0 or len(raw) + size > 65536:
                    self.send_error(413)
                    return
                raw.extend(self.rfile.read(size))
                self.rfile.read(2)
        else:
            length = int(self.headers.get("Content-Length", "0"))
            if not 0 < length <= 65536:
                self.send_error(400)
                return
            raw = self.rfile.read(length)
        body = json.loads(raw)
        (directory / "model-request.json").write_text(json.dumps(body, ensure_ascii=False), encoding="utf-8")
        reply = json.dumps({
            "choices": [{"message": {"role": "assistant", "content": "只有这封邮件的正文需要翻译。"}, "finish_reason": "stop"}]
        }, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(reply)))
        self.end_headers()
        self.wfile.write(reply)


with ThreadingHTTPServer(("127.0.0.1", 0), Model) as server:
    (directory / "fixture-port").write_text(str(server.server_port), encoding="ascii")
    server.serve_forever()
