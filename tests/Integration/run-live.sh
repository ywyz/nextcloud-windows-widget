#!/usr/bin/env bash
# In WSL Ubuntu with Docker: bash tests/Integration/run-live.sh /mnt/c/path/to/dotnet.exe
set -euo pipefail
umask 077
cd "$(dirname "$0")"
dotnet="${1:-dotnet.exe}"
testdir=$(mktemp -d)
fixture="ncw-$(openssl rand -hex 6)"
cloud="$fixture-cloud"
mail="$fixture-mail"
password=$(openssl rand -hex 20)
cleanup() {
    docker rm -fv "$cloud" "$mail" >/dev/null 2>&1 || true
    docker network rm "$fixture" >/dev/null 2>&1 || true
    rm -rf -- "$testdir"
}
trap cleanup EXIT
docker network create "$fixture" >/dev/null
docker run -d --name "$mail" --network "$fixture" --network-alias mail \
    -p 127.0.0.1::3025 -p 127.0.0.1::3143 \
    -e "GREENMAIL_OPTS=-Dgreenmail.setup.test.all -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users=one:$password@fixture.test,two:$password@fixture.test" \
    greenmail/standalone:2.1.3 >/dev/null
docker run -d --name "$cloud" --network "$fixture" -p 127.0.0.1::443 \
    -e NEXTCLOUD_ADMIN_USER=installer -e "NEXTCLOUD_ADMIN_PASSWORD=$password" \
    -e SQLITE_DATABASE=nextcloud -e "NEXTCLOUD_TRUSTED_DOMAINS=localhost 127.0.0.1" \
    nextcloud:32-apache >/dev/null
occ() { docker exec -u www-data "$cloud" php occ "$@"; }
echo "Waiting for the disposable Nextcloud installation..."
for ((i=0; i<150; i++)); do
    if docker exec "$cloud" curl --silent --fail --max-time 2 http://127.0.0.1/status.php 2>/dev/null |
        grep -q '"installed":true'; then break; fi
    sleep 2
done
occ status | grep 'installed: true'
docker exec -u www-data -e "OC_PASS=$password" "$cloud" php occ user:add fixture --password-from-env >/dev/null
openssl req -x509 -newkey rsa:2048 -noenc -days 1 -subj '/CN=localhost' \
    -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
    -addext 'basicConstraints=critical,CA:FALSE' \
    -keyout "$testdir/key" -out "$testdir/cert" 2>/dev/null
docker cp "$testdir/key" "$cloud:/etc/ssl/private/ssl-cert-snakeoil.key"
docker cp "$testdir/cert" "$cloud:/etc/ssl/certs/ssl-cert-snakeoil.pem"
docker exec "$cloud" a2enmod ssl >/dev/null
docker exec "$cloud" a2ensite default-ssl >/dev/null
docker exec "$cloud" apachectl -k graceful
cache="${XDG_CACHE_HOME:-$HOME/.cache}/nextcloud-widget-live"
mkdir -p "$cache"
install_app() {
    local app="$1" url="$2" sha="$3" archive="$cache/$1-$3.tar.gz"
    if [[ ! -f "$archive" ]] || ! echo "$sha  $archive" | sha256sum --check --status; then
        curl --silent --show-error --fail --location --retry 3 --output "$testdir/$app.tar.gz" "$url"
        echo "$sha  $testdir/$app.tar.gz" | sha256sum --check --status
        cp "$testdir/$app.tar.gz" "$archive"
    fi
    docker exec -i "$cloud" tar -xz -C /var/www/html/custom_apps < "$archive"
    docker exec "$cloud" chown -R www-data:www-data "/var/www/html/custom_apps/$app"
    occ app:enable "$app"
}
install_app tasks https://github.com/nextcloud/tasks/releases/download/v0.18.1/tasks.tar.gz \
    0c988d50531c9bfa246e6c31f3f95369adde1629ba70d15194915ab56e24687b
install_app mail https://github.com/nextcloud-releases/mail/releases/download/v5.13.3/mail-v5.13.3.tar.gz \
    c9660a13b6734b26e84b2a9f87058a96cd89905e3834beae5b6da8d1d4007d7c
occ config:system:set allow_local_remote_servers --type boolean --value=true >/dev/null
smtp_port=$(docker port "$mail" 3025/tcp | head -1 | cut -d: -f2)
python3 - "$smtp_port" <<'PY'
import sys, smtplib, time
from email.message import EmailMessage
from email.utils import formatdate
with smtplib.SMTP("127.0.0.1", int(sys.argv[1])) as smtp:
    for account in ("one", "two"):
        baseline = int(time.time()) - 3600
        for i in range(52):
            # IMAP internal dates can have one-second precision; keep the trim boundary unambiguous.
            if i == 2:
                time.sleep(2)
            message = EmailMessage()
            message["Date"] = formatdate(baseline+i, usegmt=True)
            message["From"] = "Sender <sender@fixture.test>"
            message["To"] = account+"@fixture.test"
            message["Subject"] = f"fixture-{account}-{i:02}"
            message["Message-ID"] = f"<{account}-{i}@fixture.test>"
            message.set_content("Read https://example.test/manual")
            message.add_alternative('<p>阅读 <a href="https://example.test/manual">手册</a></p>', subtype="html")
            smtp.send_message(message)
PY
for user in one two; do
    email="$user@fixture.test"
    occ mail:account:create-imap fixture "$user" "$email" mail 3143 none "$user" "$password" \
        mail 3025 none "$user" "$password"
done
imap_port=$(docker port "$mail" 3143/tcp | head -1 | cut -d: -f2)
FIXTURE_IMAP_PASSWORD="$password" python3 - "$imap_port" <<'PY'
import imaplib, os, sys
with imaplib.IMAP4("127.0.0.1", int(sys.argv[1])) as imap:
    imap.login("one", os.environ["FIXTURE_IMAP_PASSWORD"])
    assert imap.create("Corbeille")[0] == "OK"
PY
occ mail:account:sync 1
occ mail:account:sync 2
occ dav:create-calendar fixture "Widget Tasks"
export NCW_LIVE_URL="https://127.0.0.1:$(docker port "$cloud" 443/tcp | head -1 | cut -d: -f2)/"
export NCW_LIVE_PIN
NCW_LIVE_PIN=$(openssl x509 -in "$testdir/cert" -outform DER | sha256sum | cut -d' ' -f1)
export NCW_LIVE_TOKEN
NCW_LIVE_TOKEN=$(docker exec -u www-data -e "NC_PASS=$password" "$cloud" php occ user:auth-tokens:add fixture --password-from-env | tail -1)
api() {
    curl --silent --show-error --fail --cacert "$testdir/cert" -u "fixture:$NCW_LIVE_TOKEN" \
        -H 'Accept: application/json' -H 'OCS-APIRequest: true' "$@"
}
trash=$(api "${NCW_LIVE_URL}ocs/v2.php/apps/mail/ocs/mailboxes?accountId=1" | python3 -c 'import json,sys; print(next(x["databaseId"] for x in json.load(sys.stdin)["ocs"]["data"] if x["name"]=="Corbeille"))')
api -X PATCH -H 'Content-Type: application/json' -d "{\"trashMailboxId\":$trash}" \
    "${NCW_LIVE_URL}index.php/apps/mail/api/accounts/1" >/dev/null
export WSLENV="NCW_LIVE_URL:NCW_LIVE_PIN:NCW_LIVE_TOKEN${WSLENV:+:$WSLENV}"
"$dotnet" run --project "$(wslpath -w "$PWD/NextcloudWidget.Integration.csproj")"
occ mail:account:sync 1
occ mail:account:sync 2
FIXTURE_IMAP_PASSWORD="$password" python3 - "$imap_port" <<'PY'
import imaplib, os, sys
for user in ("one", "two"):
    with imaplib.IMAP4("127.0.0.1", int(sys.argv[1])) as imap:
        imap.login(user, os.environ["FIXTURE_IMAP_PASSWORD"])
        assert imap.select("INBOX", readonly=True)[0] == "OK"
        def search(*terms):
            status, result = imap.uid("SEARCH", None, *terms)
            assert status == "OK"
            return result[0].split()
        assert len(search("ALL")) == (51 if user == "one" else 52), f"Unexpected inbox count for {user}"
        assert len(search("UNSEEN")) == (49 if user == "one" else 51), f"Unexpected upstream unread count for {user}"
        if user == "one":
            assert not search("HEADER", "Message-ID", "<one-48@fixture.test>")
            assert imap.select("Corbeille", readonly=True)[0] == "OK"
            assert len(search("HEADER", "Message-ID", "<one-48@fixture.test>")) == 1
            assert len(search("UNSEEN", "HEADER", "Message-ID", "<one-48@fixture.test>")) == 1
print("PASS Exact IMAP unread counts after explicit reads; exactly one original message moved to the configured trash.")
PY
