# TweetNaCl (vendored, verbatim)

`tweetnacl.c` and `tweetnacl.h` are TweetNaCl by Daniel J. Bernstein, Bernard van Gastel, Wesley
Janssen, Tanja Lange, Peter Schwabe and Sjaak Smetsers (<https://tweetnacl.cr.yp.to/>, release
20140427). Public domain.

Provenance: copied unmodified from the mirror <https://github.com/ultramancool/tweetnacl-usable>
at commit `a8dcaa7` ("updated to latest tweetnacl"); only the two files, not the mirror's wrapper.
The hashes below let anyone compare the vendored copy against the upstream release directly —
the two are expected to be identical, but that comparison has not been made from this repository,
so do it before trusting a claim of byte-identity:

```text
sha256(tweetnacl.c) = 02e65bc3013ff2168983365e55906bc783c4c7e0a60d8100f17bb303a17175c4
sha256(tweetnacl.h) = 43f29ad721d9927b747b0100ab4160c119e7bb180c7c98a66e4bf79d31244287
```

Independent of provenance, the Ed25519 half is proven here by behaviour: `tests/test_ed25519.c`
checks the vendored code against the RFC 8032 §7.1 test vectors (seed → public key, and the
signatures), which no mirror could alter without failing `make test`.

MicroMound uses only that half — `crypto_sign` / `crypto_sign_open` and the SHA-512 they are
built on. `src/mm_ed25519.c` includes this file directly (rather than linking it) so it can reach
the field arithmetic and add three things TweetNaCl does not ship: a keypair derived from a
stored seed without a random source, and *detached* sign and verify that never copy the message
into a second buffer — the reason a constrained device can sign the canonical bytes it has
already built in place.

Why TweetNaCl and not libsodium or monocypher: it is 800 auditable lines with no build system,
which is what a first host-verified mirror needs. It is also slow (a signature costs tens of
milliseconds on a Cortex-M / ESP32 class core); when the firmware proper lands, swapping the
backend behind `include/mm_ed25519.h` is a one-file change and the tests in `tests/` are the
proof it did not change the bytes.
