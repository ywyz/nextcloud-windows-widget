"""Create an isolated v0.1 profile and serve a loopback Chat Completions fixture."""
import base64
import json
import pathlib
import sqlite3
import struct
import sys
import uuid
import zlib
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
        CREATE TABLE tasks(id TEXT PRIMARY KEY,list TEXT NOT NULL,etag TEXT,remote TEXT,local TEXT,state TEXT NOT NULL,revision INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE mail(id TEXT PRIMARY KEY,subject TEXT NOT NULL,sender TEXT NOT NULL,date TEXT NOT NULL,body TEXT NOT NULL,unread INTEGER NOT NULL);
        CREATE TABLE sync(channel TEXT PRIMARY KEY,stamp TEXT NOT NULL);
    """)
    db.execute("INSERT INTO lists(id,name,writable) VALUES(?,?,1)", (cloud + "tasks/", "Personal"))
    for i in range(1, 81):
        ics = f"BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:{i}\r\nSUMMARY:Z task {i:03d}\r\nEND:VTODO\r\nEND:VCALENDAR\r\n"
        db.execute("INSERT INTO tasks(id,list,remote,state) VALUES(?,?,?,'synced')",
                   (f"{cloud}tasks/{i}.ics", cloud + "tasks/", ics))
    for i in range(1, 4):
        body = f"Only this message body {i} should be translated."
        if i == 3:
            body += "\n" + "   \n" * 18 + "Last paragraph from the old cache."
        db.execute("INSERT INTO mail VALUES(?,?,?,?,?,1)", (
            str(i), f"QA message {i}", "Sender <fixture@example.test>",
            f"2026-01-0{i}T10:15:00.0000000+00:00", body
        ))
    for channel in ["tasks", "mail"]:
        db.execute("INSERT INTO sync VALUES(?,?)", (channel, "2026-01-03T11:00:00.0000000+00:00"))


def two_color_png(left, right):
    def chunk(name, data):
        return struct.pack(">I", len(data)) + name + data + struct.pack(">I", zlib.crc32(name + data))

    header = struct.pack(">2I5B", 64, 32, 8, 2, 0, 0, 0)
    pixels = (b"\0" + bytes(left) * 32 + bytes(right) * 32) * 32
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")


class Model(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path != "/fixture/newsletter.png":
            self.send_error(404)
            return
        if self.headers.get("Authorization") or self.headers.get("Cookie"):
            self.send_error(403)
            return
        (directory / "remote-image-request").touch()
        png = two_color_png((22, 83, 212), (242, 176, 41))
        self.send_response(200)
        self.send_header("Content-Type", "image/png")
        self.send_header("Content-Length", str(len(png)))
        self.end_headers()
        self.wfile.write(png)

    def do_POST(self):
        if self.path == "/fixture/rich-mail":
            # Add a rich body only after the application has migrated the v0.1 schema.
            png = base64.b64encode(two_color_png((234, 75, 212), (53, 203, 70))).decode("ascii")
            remote = f"http://127.0.0.1:{self.server.server_port}/fixture/newsletter.png"
            html = ("<table><tr><td><p>Photo memories</p></td></tr>\n"
                    + "\n    " * 18 + '<tr><td style="height:240px">&nbsp;</td></tr>'
                    + f'<tr><td><p>Earlier years</p><img alt="Embedded photo" src="data:image/png;base64,{png}">'
                    + f'<img alt="Newsletter photo" src="{remote}"></td></tr></table>')
            with sqlite3.connect(directory / (account.hex + ".db")) as db:
                db.execute("UPDATE mail SET body=? WHERE id='2'", ("Photo memories\nEarlier years",))
                db.execute("INSERT OR REPLACE INTO mail_content(id,rich_html) VALUES('2',?)", (html,))
            self.send_response(204)
            self.end_headers()
            return
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
