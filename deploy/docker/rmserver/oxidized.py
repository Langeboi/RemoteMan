"""Oxidized inventory: every SSH entry in RemoteMan as an Oxidized "http" source (GET /oxidized/nodes.json).

Oxidized has no API for adding devices; it reads its device list from a source. Pointing its http source here
makes every SSH entry a device, so anything added in RemoteMan is backed up without touching router.db.

- Names, addresses, ports and folders come straight from the entries table (that part of an entry is plain JSON).
- Logins are encrypted in RemoteMan with the team vault key, which this server never has. An admin's RemoteMan
  therefore seals each SSH entry's login for this server (RSA-OAEP wrapping an AES-GCM key) and stores the sealed
  blobs in the shared setting "oxidized.feed.sync". Only this server holds the private key, so other database
  logins can't read them; the server opens them when Oxidized asks.
- The private key and the access token live in remoteman_cluster.settings, which only postgres can read and which
  replicates, so every server answers with the same token.
"""
import base64
import hmac
import json
import re
import secrets

from . import common as C
from .common import log

FEED_ROW = "oxidized.feed"            # public: the server's public key (written here)
SYNC_ROW = "oxidized.feed.sync"       # written by RemoteMan: sealed logins, saved names, kept devices
CONFIG_ROW = "integration.oxidized"   # RemoteMan's Oxidized settings (group, default model, leave-out tag)
KEY = "oxidized_private_key"
TOKEN = "oxidized_token"
SSH = 2
FOLDER = 0
MAGIC = b"OX1"

# Keep in step with OxidizedFeed.cs in the app.
ICON_MODELS = {
    "cisco": "ios", "aruba": "aoscx", "mikrotik": "routeros", "fortigate": "fortios", "opnsense": "opnsense",
    "linux": "linuxgeneric", "ubuntu": "linuxgeneric", "debian": "linuxgeneric", "redhat": "linuxgeneric",
    "proxmox": "linuxgeneric",
}
TAG_ALIASES = {
    "fortinet": "fortios", "fortigate": "fortios", "mikrotik": "routeros", "aruba-cx": "aoscx", "arubaos-cx": "aoscx",
    "juniper": "junos", "arista": "eos", "edgerouter": "edgeos", "paloalto": "panos", "palo-alto": "panos",
    "linux": "linuxgeneric", "nexus": "nxos", "hp-procurve": "procurve", "dell-os10": "os10",
}
KNOWN_MODELS = {
    "ios", "iosxe", "iosxr", "nxos", "asa", "aoscx", "aosw", "procurve", "comware", "routeros", "fortios", "junos",
    "eos", "edgeos", "edgeswitch", "airos", "vyos", "vyatta", "pfsense", "opnsense", "panos", "powerconnect", "dnos",
    "os10", "ftos", "ironware", "screenos", "sonicos", "mlnxos", "linuxgeneric", "cumulus", "awplus", "netgear",
    "zynos", "tplink", "dlink", "ciscosmb", "openwrt", "routeros", "omniswitch", "firewareos", "sros", "timos",
}
MODEL_FIELD_NAMES = {"oxidized model", "oxidized"}
ENABLE_FIELD_NAMES = {"enable", "enable password", "enable secret"}


# ------------------------------------------------------------------ keys and token
def ensure_keys():
    """On the primary: create the key pair and token once, and publish the public key for RemoteMan."""
    try:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric import rsa
    except ImportError:
        log.warning("Oxidized feed off: python3-cryptography is missing")
        return
    rows = dict(C.q("SELECT key, value FROM remoteman_cluster.settings WHERE key IN (%s, %s)", (KEY, TOKEN), db=C.DB_NAME) or [])
    if KEY not in rows:
        k = rsa.generate_private_key(public_exponent=65537, key_size=3072)
        pem = k.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()).decode()
        C.q("INSERT INTO remoteman_cluster.settings VALUES (%s, %s) ON CONFLICT (key) DO NOTHING", (KEY, pem), db=C.DB_NAME)
    if TOKEN not in rows:
        new_token()
    pub = public_key_b64()
    if pub is None or C.q1("SELECT to_regclass('public.remoteman_settings') IS NOT NULL", db=C.DB_NAME) is not True:
        return
    value = json.dumps({"PublicKey": pub, "Version": 1, "Path": "/oxidized/nodes.json"})
    current = C.q1("SELECT value FROM public.remoteman_settings WHERE name = %s", (FEED_ROW,), db=C.DB_NAME)
    if current == value:
        return
    # The settings table only lets RemoteMan admins write; this is the server itself, so skip that guard.
    C.q("""BEGIN; SET LOCAL session_replication_role = replica;
        INSERT INTO public.remoteman_settings (name, value, updated_at, updated_by)
        VALUES (%s, %s, (extract(epoch FROM now()) * 1000)::bigint, 'RemoteMan server')
        ON CONFLICT (name) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at, updated_by = excluded.updated_by;
        SELECT pg_notify('remoteman', 'g'); COMMIT;""", (FEED_ROW, value), db=C.DB_NAME)
    log.info("Oxidized feed: public key published for RemoteMan")


def new_token():
    tok = secrets.token_urlsafe(32)
    C.q("""INSERT INTO remoteman_cluster.settings VALUES (%s, %s)
        ON CONFLICT (key) DO UPDATE SET value = excluded.value""", (TOKEN, tok), db=C.DB_NAME)
    return tok


def token():
    return C.q1("SELECT value FROM remoteman_cluster.settings WHERE key = %s", (TOKEN,), db=C.DB_NAME)


def _private_key():
    from cryptography.hazmat.primitives import serialization
    pem = C.q1("SELECT value FROM remoteman_cluster.settings WHERE key = %s", (KEY,), db=C.DB_NAME)
    return serialization.load_pem_private_key(pem.encode(), password=None) if pem else None


def public_key_b64():
    from cryptography.hazmat.primitives import serialization
    k = _private_key()
    if k is None:
        return None
    der = k.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    return base64.b64encode(der).decode()


def check_token(given):
    t = token()
    return bool(t and given and hmac.compare_digest(given.encode(), t.encode()))


def open_sealed(private_key, blob, entry_id):
    """base64( "OX1" | len(wrapped key) as 2 bytes | RSA-OAEP-SHA256(wrapped AES key) | nonce(12) | ciphertext+tag ), AAD = entry id."""
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric import padding
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    raw = base64.b64decode(blob)
    if raw[:3] != MAGIC:
        raise ValueError("unknown format")
    n = int.from_bytes(raw[3:5], "big")
    wrapped, rest = raw[5:5 + n], raw[5 + n:]
    key = private_key.decrypt(wrapped, padding.OAEP(mgf=padding.MGF1(hashes.SHA256()), algorithm=hashes.SHA256(), label=None))
    plain = AESGCM(key).decrypt(rest[:12], rest[12:], entry_id.encode())
    return json.loads(plain)


# ------------------------------------------------------------------ the node list
def _json(s):
    try:
        return json.loads(s) if s else {}
    except ValueError:
        return {}


def split_tags(tags):
    return [t.strip().lower() for t in re.split(r"[,;]", tags or "") if t.strip()]


NAME_MODELS = {
    "ros": "routeros", "chr": "routeros", "routeros": "routeros", "mikrotik": "routeros", "crs": "routeros", "ccr": "routeros",
    "fgt": "fortios", "fortigate": "fortios", "pve": "linuxgeneric", "proxmox": "linuxgeneric",
}


def model_for(payload, default_model, name="", chosen=None):
    """Picked in the device list, then custom field "Oxidized model", a tag naming a model, the vendor icon,
    a word in the name, then the default."""
    if chosen and str(chosen).strip():
        return str(chosen).strip()
    for f in payload.get("Fields") or []:
        if (f.get("Name") or "").strip().lower() in MODEL_FIELD_NAMES and not f.get("Secret") and (f.get("Value") or "").strip():
            return f["Value"].strip()
    for t in split_tags(payload.get("Tags")):
        if t in KNOWN_MODELS:
            return t
        if t in TAG_ALIASES:
            return TAG_ALIASES[t]
    icon = (payload.get("Icon") or "").strip().lower()
    if icon in ICON_MODELS:
        return ICON_MODELS[icon]
    for w in re.split(r"[^A-Za-z0-9]+", name or ""):
        if w.lower() in NAME_MODELS:
            return NAME_MODELS[w.lower()]
    return default_model or None


def build_nodes(rows, config, sync, open_login=None):
    """rows: (id, parent_id, kind, name, payload, deleted). Returns Oxidized's node list."""
    group_mode = (config.get("FeedGroup") or "None").lower()
    default_model = (config.get("FeedDefaultModel") or "").strip()
    leave_out = (config.get("FeedExcludeTag") or "no-oxidized").strip().lower()
    send_logins = config.get("FeedSendLogins", True) is not False
    auto_add = config.get("FeedAutoAdd", True) is not False
    excluded = {str(x).lower() for x in sync.get("Excluded") or []}
    included = {str(x).lower() for x in sync.get("Included") or []}
    links = sync.get("Links") or {}
    models = {str(k).lower(): v for k, v in (sync.get("Models") or {}).items()}
    logins = sync.get("Logins") or {}

    folders = {}
    for r in rows:
        if r[2] == FOLDER and not r[5]:
            folders[str(r[0]).lower()] = (str(r[1]).lower() if r[1] else None, r[3])

    def top_folder(parent):
        name, hops = None, 0
        while parent and parent in folders and hops < 64:
            parent, name = folders[parent][0], folders[parent][1]
            hops += 1
        return name

    out, seen = [], set()
    for r in sorted((r for r in rows if r[2] == SSH and not r[5]), key=lambda r: str(r[0]).lower()):
        eid = str(r[0]).lower()
        if eid in excluded or (not auto_add and eid not in included):
            continue
        p = _json(r[4])
        if leave_out and leave_out in split_tags(p.get("Tags")):
            continue
        eps = [e for e in (p.get("Endpoints") or []) if (e.get("Host") or "").strip()]
        if not eps:
            continue
        host, port = eps[0]["Host"].strip(), int(eps[0].get("Port") or 0) or 22
        link = p.get("OxidizedNode") or links.get(eid) or links.get(str(r[0]))
        if link:
            group, _, name = link.rpartition("/")
            group = group or None
        else:
            name = (r[3] or "").strip() or host
            group = top_folder(str(r[1]).lower() if r[1] else None) if group_mode in ("topfolder", "folder") else None
        # Oxidized finds devices by name alone, so names are unique across groups.
        if name.lower() in seen:
            name = f"{name}-{host}"
        seen.add(name.lower())
        model = model_for(p, default_model, r[3] or "", models.get(eid))
        node = {"name": name, "ip": host, "model": model, "group": group, "remoteman_id": eid}
        if port != 22:
            node["ssh_port"] = port
        sealed = (logins.get(eid) or logins.get(str(r[0])) or {}).get("S")
        if send_logins and sealed and open_login:
            try:
                login = open_login(sealed, eid)
                if login.get("u"):
                    node["username"] = login["u"]
                if login.get("p"):
                    node["password"] = login["p"]
                if login.get("e"):
                    node["enable"] = login["e"]
            except Exception as e:  # noqa: BLE001
                log.debug("oxidized: login for %s can't be opened: %s", name, e)
        out.append({k: v for k, v in node.items() if v is not None})

    for k in sync.get("Keep") or []:
        name = (k.get("Name") or "").strip()
        if not name or name.lower() in seen:
            continue
        seen.add(name.lower())
        out.append({x: y for x, y in {"name": name, "ip": k.get("Ip") or name, "model": k.get("Model") or default_model or None,
                                     "group": k.get("Group") or None}.items() if y})
    return out


def nodes():
    """Reads this server's copy of the database (a standby works too)."""
    rows = C.q("SELECT id, parent_id, kind, name, payload, deleted FROM remoteman_entries", db=C.DB_NAME) or []
    settings = dict(C.q("SELECT name, value FROM remoteman_settings WHERE name IN (%s, %s)", (CONFIG_ROW, SYNC_ROW), db=C.DB_NAME) or [])
    key = None
    try:
        key = _private_key()
    except Exception as e:  # noqa: BLE001
        log.warning("oxidized: private key unreadable: %s", e)
    opener = (lambda blob, eid: open_sealed(key, blob, eid)) if key else None
    return build_nodes(rows, _json(settings.get(CONFIG_ROW)), _json(settings.get(SYNC_ROW)), opener)


def config_snippet(address, tok):
    return f"""source:
  default: http
  http:
    url: https://{address}:{C.ADMIN_PORT}/oxidized/nodes.json
    secure: false          # the RemoteMan server's certificate is self-signed
    read_timeout: 30
    headers:
      Authorization: "Bearer {tok}"
    map:
      name: name
      ip: ip
      model: model
      group: group
      username: username
      password: password
    vars_map:
      ssh_port: ssh_port
      enable: enable
"""
