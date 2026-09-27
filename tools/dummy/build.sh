#!/bin/sh
# Builds the selftest dummy target (needs mingw-w64)
cd "$(dirname "$0")" && x86_64-w64-mingw32-gcc -O1 -s -static -o vanta_dummy.exe vanta_dummy.c dummy_asm.S
