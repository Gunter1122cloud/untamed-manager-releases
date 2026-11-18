"""Obsidian bot adapter transport. Standard library; no Discord token here.

Call only after freshly verifying actual Discord member roles and private
channel permissions. Never accept host/credentials/role IDs from user input.
"""
import asyncio
import hashlib
import hmac
import ipaddress
import json
import socket
import ssl
import struct
import uuid
from datetime import datetime, timezone
from dataclasses import dataclass, field

@dataclass(frozen=True)
class Host:
    address: str
    port: int
    certificate_sha256: str
    credential: str = field(repr=False)

    def __post_init__(self):
        ip = ipaddress.ip_address(self.address)
        allowed = ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8")
        if ip.version != 4 or not any(ip in ipaddress.ip_network(n) for n in allowed):
            raise ValueError("Only private IPv4 hosts allowed")
        if not 1024 <= self.port <= 65535:
            raise ValueError("Invalid port")
        if len(bytes.fromhex(self.certificate_sha256)) != 32 or len(bytes.fromhex(self.credential)) != 32:
            raise ValueError("Invalid pin or credential")

def read_exact(stream, length):
    parts = bytearray()
    while len(parts) < length:
        chunk = stream.recv(length - len(parts))
        if not chunk:
            raise ConnectionError("Incomplete reply: operation may be uncertain")
        parts.extend(chunk)
    return bytes(parts)

def call(host, *, guild_id, channel_id, user_id, role_ids, kind,
         request_id=None, timeout=120, **fields):
    # TLS private CA alternative: exact certificate pin, verified BEFORE
    # any credential or other request data is sent. No unpinned fallback.
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    operation = str(uuid.UUID(str(request_id))) if request_id else str(uuid.uuid4())
    envelope = dict(fields)
    envelope.update(requestId=operation, credential=host.credential,
                    guildId=str(guild_id), channelId=str(channel_id),
                    userId=str(user_id), roleIds=[str(r) for r in role_ids],
                    sentUtc=datetime.now(timezone.utc).isoformat(), kind=kind)
    payload = json.dumps(envelope, ensure_ascii=False).encode("utf-8")
    if not 2 <= len(payload) <= 16384:
        raise ValueError("Request exceeds protocol limit")
    with socket.create_connection((host.address, host.port), timeout=10) as sock:
        with context.wrap_socket(sock, server_hostname="Obsidian-Discord-Control") as tls:
            actual = hashlib.sha256(tls.getpeercert(binary_form=True)).hexdigest()
            if not hmac.compare_digest(actual, host.certificate_sha256.lower()):
                raise ssl.SSLError("Obsidian certificate pin mismatch")
            tls.settimeout(timeout)
            tls.sendall(struct.pack(">I", len(payload)) + payload)
            length = struct.unpack(">I", read_exact(tls, 4))[0]
            if not 2 <= length <= 1048576:
                raise ValueError("Invalid response length")
            reply = json.loads(read_exact(tls, length))
            if not isinstance(reply, dict) or not isinstance(reply.get("ok"), bool):
                raise ValueError("Unverified reply")
            return reply

async def async_call(host, **kwargs):
    # No automatic retry. Preserve operation_id externally before apply.
    return await asyncio.to_thread(call, host, **kwargs)
