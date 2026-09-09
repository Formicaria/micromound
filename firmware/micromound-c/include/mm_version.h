/*
 * The library's version — the same string as <MicromoundVersion> in Directory.Build.props, kept in
 * step by scripts/validate.sh and validate.ps1 (a mismatch fails the guard). A board reports it in
 * the port server's hello and a bench operator reads it from the log.
 */
#ifndef MM_VERSION_H
#define MM_VERSION_H

#define MM_VERSION "0.9.36"

#endif
