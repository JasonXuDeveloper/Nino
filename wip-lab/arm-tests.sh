#!/bin/bash
# The nullable, padding, layout and golden-bytes tests of the net10.0 build under qemu-aarch64 (Neoverse-N1), by reflection.
exec 9>/tmp/nino-heavy.lock; flock 9
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 qemu-aarch64-static -cpu neoverse-n1 -L /usr/aarch64-linux-gnu /tmp/claude-0/-workspace/91aad9ce-3bb6-50d1-a5d8-163511cc7c13/scratchpad/lab/armrun/out-arm64/armrun /tmp/claude-0/-workspace/91aad9ce-3bb6-50d1-a5d8-163511cc7c13/scratchpad/simd-clone/tests/Nino.UnitTests/bin/Release/net10.0/Nino.UnitTests.dll "${@:-Nullable Padding GoldenBytes}" 9>&- 2>&1 | tail -40
