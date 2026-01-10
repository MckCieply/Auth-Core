#!/usr/bin/env python3
"""Verify an RS256 JWT against a JWKS, the way a consumer service would.

Usage: python3 scripts/verify_jwt.py <jwt> <jwks_url> <issuer> <audience>

Exit status: 0 when the token is valid, 1 when it is not (a short reason goes to stderr).
The token itself is never printed. Requires PyJWT with the crypto extra (pip install 'PyJWT[crypto]').
"""
import sys

import jwt
from jwt import PyJWKClient


def main(argv: list[str]) -> int:
    if len(argv) != 5:
        print("usage: verify_jwt.py <jwt> <jwks_url> <issuer> <audience>", file=sys.stderr)
        return 2
    token, jwks_url, issuer, audience = argv[1:]
    try:
        signing_key = PyJWKClient(jwks_url).get_signing_key_from_jwt(token)
        jwt.decode(
            token,
            signing_key.key,
            algorithms=["RS256"],
            audience=audience,
            issuer=issuer,
            options={"require": ["exp", "iat", "sub"]},
        )
    except Exception as exc:  # noqa: BLE001 - any failure means "invalid"; report the class and message only
        print(f"INVALID: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1
    print("VALID: RS256 signature, iss, aud, exp, iat and sub verified against JWKS")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
