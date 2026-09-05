#include "mm_test.h"

#include <stdlib.h>

int mm_test_checks = 0;
int mm_test_failures = 0;
const char *mm_test_golden_dir = "../../tests/Micromound.Tests/Golden/files";

static void run(const char *name, void (*fn)(void))
{
    int before = mm_test_failures;
    printf("%s\n", name);
    fn();
    printf("  %s\n", mm_test_failures == before ? "ok" : "FAILED");
}

int main(int argc, char **argv)
{
    if (argc > 1) mm_test_golden_dir = argv[1];

    run("json writer: structure and the escaping rule", test_json);
    run("double formatting: .NET's layout rule", test_format);
    run("sha256: FIPS 180-4 vectors", test_sha256);
    run("ed25519: RFC 8032 vectors, detached sign/verify, canonical S", test_ed25519);
    run("envelope: canonical bytes, digest, signature splice, verify", test_envelope);
    run("golden fixtures: byte-for-byte against tests/Micromound.Tests/Golden/files", test_golden);

    printf("\n%d checks, %d failed\n", mm_test_checks, mm_test_failures);
    return mm_test_failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
