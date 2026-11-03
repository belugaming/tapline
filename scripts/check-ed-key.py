#!/usr/bin/env python3
"""Checks an update's EdDSA (Ed25519) signature against the public key Tapline ships with
(SUPublicEDKey), so a release never goes out signed with a key the app would reject.

    python3 scripts/check-ed-key.py <public key, base64> <file> <signature, base64>

Plain Python, no packages: Ed25519 verification as in RFC 8032, section 5.1.7.
"""
import base64
import hashlib
import sys

p = 2**255 - 19
L = 2**252 + 27742317777372353535851937790883648493
d = -121665 * pow(121666, p - 2, p) % p
I = pow(2, (p - 1) // 4, p)


def recover_x(y, sign):
    xx = (y * y - 1) * pow(d * y * y + 1, p - 2, p)
    x = pow(xx, (p + 3) // 8, p)
    if (x * x - xx) % p:
        x = x * I % p
    if (x * x - xx) % p:
        raise ValueError("not a point")
    if x & 1 != sign:
        x = p - x
    return x


def decode_point(b):
    y = int.from_bytes(b, "little")
    sign = y >> 255
    y &= (1 << 255) - 1
    if y >= p:
        raise ValueError("not a point")
    x = recover_x(y, sign)
    return (x, y, 1, x * y % p)


def add(P, Q):
    A = (P[1] - P[0]) * (Q[1] - Q[0]) % p
    B = (P[1] + P[0]) * (Q[1] + Q[0]) % p
    C = 2 * P[3] * Q[3] * d % p
    D = 2 * P[2] * Q[2] % p
    E, F, G, H = B - A, D - C, D + C, B + A
    return (E * F % p, G * H % p, F * G % p, E * H % p)


def multiply(s, P):
    Q = (0, 1, 1, 0)
    while s:
        if s & 1:
            Q = add(Q, P)
        P = add(P, P)
        s >>= 1
    return Q


def equal(P, Q):
    return (P[0] * Q[2] - Q[0] * P[2]) % p == 0 and (P[1] * Q[2] - Q[1] * P[2]) % p == 0


G = (recover_x(4 * pow(5, p - 2, p) % p, 0), 4 * pow(5, p - 2, p) % p, 1, 0)
G = (G[0], G[1], 1, G[0] * G[1] % p)


def verify(public, message, signature):
    if len(public) != 32 or len(signature) != 64:
        return False
    A = decode_point(public)
    R = decode_point(signature[:32])
    s = int.from_bytes(signature[32:], "little")
    if s >= L:
        return False
    h = int.from_bytes(hashlib.sha512(signature[:32] + public + message).digest(), "little") % L
    return equal(multiply(s, G), add(R, multiply(h, A)))


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    public = base64.b64decode(sys.argv[1])
    message = open(sys.argv[2], "rb").read()
    signature = base64.b64decode(sys.argv[3])
    if not verify(public, message, signature):
        sys.exit(f"error: the signature of {sys.argv[2]} does not match SUPublicEDKey {sys.argv[1]}")
    print(f"{sys.argv[2]}: signature matches SUPublicEDKey")


if __name__ == "__main__":
    main()
