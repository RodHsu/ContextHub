"""Loopback-only synthetic TLS server; all private material stays in memory."""

import datetime
import hashlib
import json
import os
import socket
import ssl
import sys

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID


def main():
    mode = sys.argv[1]
    if mode not in {"normal", "reflect", "escaped-reflect", "redirect", "failure", "wrong-pin", "normal-trust-reject"}:
        raise ValueError("fixture mode rejected")
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
    now = datetime.datetime.now(datetime.timezone.utc)
    certificate = (
        x509.CertificateBuilder().subject_name(name).issuer_name(name)
        .public_key(key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(minutes=1))
        .not_valid_after(now + datetime.timedelta(minutes=5)).sign(key, hashes.SHA256())
    )
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    fd = os.memfd_create("synthetic-loopback-tls", os.MFD_CLOEXEC)
    try:
        material = certificate.public_bytes(serialization.Encoding.PEM) + key.private_bytes(
            serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()
        )
        remaining = memoryview(material)
        while remaining:
            remaining = remaining[os.write(fd, remaining):]
        context.load_cert_chain(f"/proc/self/fd/{fd}")
        del material, remaining, key
    finally:
        os.close(fd)
    result = {"ApiKeyPresent": False, "AuthorizationAbsent": True, "RequestReceived": False}
    with socket.socket() as listener:
        listener.settimeout(8)
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        print(json.dumps({"Port": listener.getsockname()[1], "CertificatePin": hashlib.sha256(
            certificate.public_bytes(serialization.Encoding.DER)).hexdigest()}), flush=True)
        try:
            raw, _ = listener.accept()
            with raw:
                raw.settimeout(8)
                with context.wrap_socket(raw, server_side=True) as connection:
                    request = b""
                    while b"\r\n\r\n" not in request:
                        chunk = connection.recv(4096)
                        if not chunk or len(request) + len(chunk) > 16384:
                            raise ValueError("fixture request rejected")
                        request += chunk
                    headers = request.split(b"\r\n\r\n", 1)[0].split(b"\r\n")[1:]
                    api_key = b""
                    for header in headers:
                        field, _, value = header.partition(b":")
                        if field.lower() == b"x-api-key":
                            api_key = value.strip()
                        if field.lower() == b"authorization":
                            result["AuthorizationAbsent"] = False
                    result.update(ApiKeyPresent=bool(api_key), RequestReceived=True)
                    body = api_key if mode == "reflect" else b'{"Id":"fixture-id"}'
                    if mode == "escaped-reflect":
                        body = ('{"nested":[{"value":"' + ''.join(f'\\u{character:04x}' for character in api_key)
                                 + '"}]}').encode()
                    status = "302 Found" if mode == "redirect" else "403 Forbidden" if mode == "failure" else "200 OK"
                    location = "Location: https://example.invalid/forbidden\r\n" if mode == "redirect" else ""
                    connection.sendall((f"HTTP/1.1 {status}\r\n{location}Content-Type: application/json\r\n"
                                        f"Content-Length: {len(body)}\r\nConnection: close\r\n\r\n").encode() + body)
                    del request, headers, api_key, body
        except (OSError, ValueError):
            # Rejected handshakes and transport failures reveal no exception details.
            pass
    print(json.dumps(result), flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print('{"FixtureFailed":true}', flush=True)
        sys.exit(1)
