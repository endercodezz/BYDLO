#!/usr/bin/env python3
"""Standalone Linux VPS installer. Importing this module never performs I/O."""
import argparse
import hashlib
import io
import ipaddress
import json
import os
from pathlib import Path
import platform
import re
import secrets
import shutil
import subprocess
import tempfile
from urllib.parse import urlencode, quote
from urllib.request import urlopen
import uuid
import zipfile

VERSION = "26.10.21"
ASSETS = {
    "x86_64": ("64", "0eec3ed4163c273d8c8bf821f7d219e257921cf584aa3fb970715e458e631978"),
    "aarch64": ("arm64-v8a", "d1304946c486d98e6b886e9b931eaaaebb23835b30e217638037b7f2721d4e9c"),
}
ROOT = Path("/opt/bydlo-reality")
UNIT = Path("/etc/systemd/system/bydlo-reality.service")
SERVICE = """[Unit]
Description=BYDLO VLESS REALITY server (Xray-core)
After=network-online.target
Wants=network-online.target

[Service]
DynamicUser=yes
ExecStart=/opt/bydlo-reality/xray run -config /opt/bydlo-reality/config.json
Restart=on-failure
RestartSec=5
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
ReadOnlyPaths=/opt/bydlo-reality
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
"""


def hostname(value):
    if len(value) > 253 or not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?", value):
        raise ValueError("Укажите корректное DNS-имя без схемы и порта")
    if any(not label or len(label) > 63 or label.startswith("-") or label.endswith("-")
           for label in value.split(".")):
        raise ValueError("Некорректное DNS-имя")
    return value.lower()


def server_address(value):
    try:
        return str(ipaddress.ip_address(value.strip("[]")))
    except ValueError:
        return hostname(value)


def make_config(client_id, private_key, short_id, sni, port):
    if not 1 <= port <= 65535:
        raise ValueError("Порт должен быть в диапазоне 1–65535")
    sni = hostname(sni)
    return {
        "log": {"loglevel": "warning"},
        "inbounds": [{"listen": "0.0.0.0", "port": port, "protocol": "vless",
            "settings": {"clients": [{"id": str(uuid.UUID(client_id)), "flow": "xtls-rprx-vision"}],
                         "decryption": "none"},
            "streamSettings": {"network": "tcp", "security": "reality", "realitySettings": {
                "show": False, "target": sni + ":443", "serverNames": [sni],
                "privateKey": private_key, "shortIds": [short_id]}}}],
        "outbounds": [{"protocol": "freedom", "tag": "direct"}],
    }


def make_link(address, port, client_id, public_key, short_id, sni):
    address = server_address(address)
    host = "[" + address + "]" if ":" in address else address
    query = urlencode({"encryption": "none", "security": "reality", "sni": hostname(sni),
                       "fp": "chrome", "pbk": public_key, "sid": short_id,
                       "type": "tcp", "flow": "xtls-rprx-vision"})
    return f"vless://{uuid.UUID(client_id)}@{host}:{port}?{query}#{quote('BYDLO VPS')}"


def parse_keys(output):
    pairs = dict(line.split(":", 1) for line in output.splitlines() if ":" in line)
    private = pairs.get("PrivateKey", pairs.get("Private key", "")).strip()
    public = pairs.get("Password (PublicKey)", pairs.get("PublicKey", pairs.get("Public key", ""))).strip()
    if not all(re.fullmatch(r"[A-Za-z0-9_-]{43}", key) for key in (private, public)):
        raise ValueError("Xray не вернул пару X25519; установка остановлена")
    return private, public


def install(args):
    # Refuse before any subprocess, download or filesystem mutation on agent machines.
    if os.environ.get("BYDLO_SAFE_DEV") == "1" or os.environ.get("BYDLO_MOCK") == "1":
        raise RuntimeError("Установка запрещена в безопасной разработке / Mock Mode")
    if platform.system() != "Linux" or os.geteuid() != 0 or not Path("/run/systemd/system").is_dir():
        raise RuntimeError("Нужен отдельный Linux VPS с systemd и запуск от root")
    if ROOT.exists() or UNIT.exists():
        raise RuntimeError("Установка уже существует; существующие ключи и службы не изменены")
    if not shutil.which("systemctl"):
        raise RuntimeError("systemctl не найден")
    asset, digest = ASSETS[platform.machine()]
    address, sni = server_address(args.address), hostname(args.sni)
    if not 1 <= args.port <= 65535:
        raise ValueError("Некорректный порт")
    with tempfile.TemporaryDirectory(prefix="bydlo-reality-") as staging:
        staged = Path(staging)
        url = f"https://github.com/XTLS/Xray-core/releases/download/v{VERSION}/Xray-linux-{asset}.zip"
        with urlopen(url, timeout=60) as response:
            data = response.read(100 * 1024 * 1024 + 1)
        if len(data) > 100 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != digest:
            raise RuntimeError("SHA-256 архива Xray не совпадает; установка остановлена")
        # Extract only an explicitly named regular binary, never archive paths or symlinks.
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            (staged / "xray").write_bytes(archive.read("xray"))
        (staged / "xray").chmod(0o755)
        output = subprocess.run([str(staged / "xray"), "x25519"], check=True,
                                capture_output=True, text=True).stdout
        private, public = parse_keys(output)
        client_id, short_id = str(uuid.uuid4()), secrets.token_hex(8)
        config = make_config(client_id, private, short_id, sni, args.port)
        config_file = staged / "config.json"
        config_file.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
        config_file.chmod(0o600)
        subprocess.run([str(staged / "xray"), "run", "-test", "-config", str(config_file)],
                       check=True, capture_output=True)
        link = make_link(address, args.port, client_id, public, short_id, sni)
        old_umask = os.umask(0o077)
        try:
            ROOT.mkdir(mode=0o755)
            ROOT.chmod(0o755)
            shutil.copy2(staged / "xray", ROOT / "xray")
            # DynamicUser reads credentials through systemd's protected runtime directory.
            shutil.copy2(config_file, ROOT / "config.json")
            (ROOT / "client-link.txt").write_text(link + "\n", encoding="utf-8")
            UNIT.write_text(SERVICE.replace("-config /opt/bydlo-reality/config.json",
                "-config %d/config.json").replace("DynamicUser=yes", "DynamicUser=yes\nLoadCredential=config.json:/opt/bydlo-reality/config.json"),
                encoding="utf-8")
            UNIT.chmod(0o644)
            subprocess.run(["systemctl", "daemon-reload"], check=True)
            subprocess.run(["systemctl", "enable", "--now", "bydlo-reality.service"], check=True)
            subprocess.run(["systemctl", "is-active", "--quiet", "bydlo-reality.service"], check=True)
        except BaseException:
            # Keep files for diagnosis; never stop or change another service.
            raise RuntimeError("Установка не завершена; файлы сохранены в /opt/bydlo-reality. Проверьте systemctl status bydlo-reality") from None
        finally:
            os.umask(old_umask)
    print("Установлен отдельный bydlo-reality.service. Ссылка содержит данные доступа:")
    print(link)
    print("Копия: /opt/bydlo-reality/client-link.txt (доступ только root).")


def main():
    parser = argparse.ArgumentParser(description="Установка Xray VLESS + REALITY на отдельном VPS")
    parser.add_argument("--address", required=True, help="Публичный IPv4/IPv6 или DNS-имя VPS")
    parser.add_argument("--sni", required=True, help="Доступный с VPS TLS 1.3 сайт для REALITY")
    parser.add_argument("--port", type=int, default=443)
    parser.add_argument("--install", action="store_true", help="Скачать Xray и создать отдельную службу")
    args = parser.parse_args()
    if not args.install:
        parser.error("Для изменения VPS явно укажите --install. Сначала прочитайте docs/VPS.md")
    try:
        install(args)
    except (RuntimeError, ValueError, KeyError, OSError, subprocess.SubprocessError) as exc:
        # Never include subprocess stdout/stderr (keys/config) in errors.
        parser.exit(1, f"Установка остановлена: {type(exc).__name__}. Проверьте требования в docs/VPS.md.\n")


if __name__ == "__main__":
    main()
