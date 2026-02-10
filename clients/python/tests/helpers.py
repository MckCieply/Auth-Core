"""Test tools: keys made here, tokens signed here, stand-ins for the JWKS URL, a clock we move by hand."""

import base64
import hashlib
import hmac
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import jwt
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

ISSUER = "http://localhost:8088/auth"
AUDIENCE = "notes-api"
JWKS_URL = "http://auth.test/auth/.well-known/jwks.json"

MISSING = object()  # as a claim or header override: leave it out


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def b64url_int(number: int) -> str:
    return b64url(number.to_bytes((number.bit_length() + 7) // 8, "big"))


def b64url_decode(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


class Signer:
    """An RSA key with a `kid`: signs tokens, and publishes its public half as a JWK."""

    def __init__(self, kid: str) -> None:
        self.kid = kid
        self.private_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = self.private_key.public_key().public_numbers()
        self.jwk = {
            "kty": "RSA", "use": "sig", "alg": "RS256", "kid": kid,
            "n": b64url_int(numbers.n), "e": b64url_int(numbers.e),
        }
        self.public_pem = self.private_key.public_key().public_bytes(
            serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo
        )

    def claims(self, **overrides) -> dict:
        now = int(time.time())
        claims = {
            "iss": ISSUER, "aud": AUDIENCE, "sub": "user-1", "org_id": "11111111-1111-1111-1111-111111111111",
            "roles": ["user"], "permissions": ["notes:read", "notes:write"], "iat": now, "exp": now + 600,
        }
        for name, value in overrides.items():
            if value is MISSING:
                claims.pop(name, None)
            else:
                claims[name] = value
        return claims

    def token(self, headers: dict | None = None, algorithm: str = "RS256", **overrides) -> str:
        header = {"kid": self.kid, "typ": "at+jwt"}
        header.update(headers or {})
        header = {name: value for name, value in header.items() if value is not MISSING}
        return jwt.encode(self.claims(**overrides), self.private_key, algorithm=algorithm, headers=header)


def jws(header: dict, payload: dict, signature: bytes = b"") -> str:
    """A token made by hand, for the ones PyJWT will not sign."""
    parts = [b64url(json.dumps(header).encode()), b64url(json.dumps(payload).encode())]
    return ".".join(parts) + "." + b64url(signature)


def hs256_with_public_key(signer: Signer, **overrides) -> str:
    """The algorithm-confusion token: HS256, the public key as the secret."""
    header = {"alg": "HS256", "typ": "at+jwt", "kid": signer.kid}
    signing_input = ".".join(
        [b64url(json.dumps(header).encode()), b64url(json.dumps(signer.claims(**overrides)).encode())]
    )
    mac = hmac.new(signer.public_pem, signing_input.encode(), hashlib.sha256).digest()
    return signing_input + "." + b64url(mac)


def with_changed_signature(token: str) -> str:
    head, payload, signature = token.split(".")
    raw = bytearray(b64url_decode(signature))
    raw[0] ^= 0xFF
    return ".".join([head, payload, b64url(bytes(raw))])


def with_changed_payload(token: str, **changes) -> str:
    head, payload, signature = token.split(".")
    claims = json.loads(b64url_decode(payload))
    claims.update(changes)
    return ".".join([head, b64url(json.dumps(claims).encode()), signature])


class FakeJwks:
    """Stands for the JWKS URL: counts the fetches, serves the key set it is given, fails when told to."""

    def __init__(self, *signers: Signer) -> None:
        self.publish(*signers)
        self.fails = False
        self.fetches = 0

    def publish(self, *signers: Signer) -> None:
        self.document = {"keys": [signer.jwk for signer in signers]}

    def __call__(self, url: str, timeout: float) -> dict:
        self.fetches += 1
        if self.fails:
            raise OSError("the key set is down")
        return self.document


class Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += seconds


def bearer(token: str) -> dict:
    return {"Authorization": f"Bearer {token}"}


def assert_unauthorized(response) -> None:
    assert response.status_code == 401
    assert response.content == b""
    assert response.headers["www-authenticate"] == "Bearer"
    assert response.headers["cache-control"] == "no-store"


def assert_unavailable(response) -> None:
    assert response.status_code == 503
    assert response.content == b'{"error":"auth_unavailable"}'
    assert response.headers["content-type"] == "application/json"
    assert response.headers["cache-control"] == "no-store"


class SlowJwks(FakeJwks):
    def __init__(self, *signers):
        super().__init__(*signers)
        self.started = threading.Event()
        self.release = threading.Event()

    def __call__(self, url, timeout):
        self.started.set()
        assert self.release.wait(10)
        return super().__call__(url, timeout)


class JwksServer:
    """Serves `document` at /jwks, after `delay` seconds, with `status`, one byte every `trickle` seconds if that is set."""

    def __init__(self, document: dict) -> None:
        self.document = document
        self.status = 200
        self.delay = 0.0
        self.trickle = 0.0
        self.requests = 0
        server = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                server.requests += 1
                time.sleep(server.delay)
                body = json.dumps(server.document).encode()
                self.send_response(server.status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                try:
                    if server.trickle:
                        for index in range(len(body)):
                            self.wfile.write(body[index:index + 1])
                            self.wfile.flush()
                            time.sleep(server.trickle)
                    else:
                        self.wfile.write(body)
                except OSError:
                    pass  # the client gave up

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.httpd.server_port}/jwks"
        threading.Thread(target=self.httpd.serve_forever, args=(0.05,), daemon=True).start()

    def close(self) -> None:
        self.httpd.shutdown()
        self.httpd.server_close()

    def __enter__(self) -> "JwksServer":
        return self

    def __exit__(self, *exc_info) -> None:
        self.close()
