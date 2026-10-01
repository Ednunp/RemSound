# remsound-opus.dll - libopus with custom modes, for the ASIO "Jamulus-style frames" switch

libopus 1.6.1 (downloads.xiph.org/releases/opus/opus-1.6.1.tar.gz, SHA-256
6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1, matching Xiph's SHA256SUMS.txt), built
2026-09-26 with portable tools in D:\proj\toolchain (no installer): llvm-mingw 20260922 (ucrt, clang 23.1.2),
CMake 4.4.3, Ninja 1.13.2. BSD licence: COPYING in this folder.

Why a separate DLL: the normal Opus path keeps Concentus.Native's opus.dll (built without custom modes). This build
adds OPUS_CUSTOM_MODES (64- and 128-sample frames, as Jamulus uses) and is tuned for speed, and is loaded only when
the switch is on - so the normal path is untouched and an A/B comparison is fair.

Options: Release -O3; CUSTOM_MODES on; FLOAT_APPROX on (x64 Windows is IEEE 754); float API on; run-time CPU
detection for SSE4.1 and AVX2 (presumes only SSE/SSE2, which every x64 CPU has); stack protector and fortify off (they
would pull in libssp); DRED/OSCE (deep-learning features) off. Imports only KERNEL32 and the Windows UCRT.

Rebuild (Git Bash):

    export PATH="/d/proj/toolchain/llvm-mingw-20260922-ucrt-x86_64/bin:/d/proj/toolchain/ninja:/d/proj/toolchain/cmake-4.4.3-windows-x86_64/bin:$PATH"
    cmake -S opus-1.6.1 -B build-opus -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang \
      -DCMAKE_C_FLAGS_RELEASE="-O3 -DNDEBUG" -DBUILD_SHARED_LIBS=ON -DOPUS_BUILD_SHARED_LIBRARY=ON \
      -DOPUS_CUSTOM_MODES=ON -DOPUS_FLOAT_APPROX=ON -DOPUS_STACK_PROTECTOR=OFF -DOPUS_FORTIFY_SOURCE=OFF \
      -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_X86_MAY_HAVE_SSE4_1=ON -DOPUS_X86_MAY_HAVE_AVX2=ON
    cmake --build build-opus -j 16
    cp build-opus/libopus.dll remsound-opus.dll
