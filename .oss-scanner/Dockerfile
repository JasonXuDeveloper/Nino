# Build environment for Anthropic's OSS Scanner (https://github.com/anthropics/oss-scanner, projects/nino).
# The scanner builds this file with the repository as the build context, then runs the image without network
# access: every toolchain, package and test dependency is fetched here. Works on x86-64 and Arm64 hosts.
#
#   .NET      SDK 10 and the 8.0 runtimes (Nino.Core's net8.0 / net10.0 / netstandard builds, the tests, the fuzzer)
#   Mono      6.12 from mono-project.com, the newest packaged Mono and the line Unity's Mono fork is based on,
#             runs the net472 build (Nino.Core's netstandard2.1 build, as Unity loads it). .NET ships no
#             Linux runtime pack for its own Mono after .NET 9 previews, so there is no newer Mono to add.
#   Go        latest stable (module says Go 1.22)
#   Rust      stable and nightly (cargo-fuzz)
#   Node.js   24 (the TypeScript tests run .ts files directly)
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_ROOT=/opt/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/opt/nuget \
    GOTOOLCHAIN=local \
    GOFLAGS=-mod=mod \
    GOPATH=/opt/go-path \
    RUSTUP_HOME=/opt/rustup \
    CARGO_HOME=/opt/cargo \
    NINO_CORPUS=/src/tests/conformance/corpus
ENV PATH=/opt/dotnet:/opt/go/bin:/opt/go-path/bin:/opt/cargo/bin:/opt/node/bin:$PATH

RUN apt-get update && apt-get install -y --no-install-recommends \
      ca-certificates curl git gnupg build-essential clang lld pkg-config python3 unzip xz-utils libicu74 \
    && rm -rf /var/lib/apt/lists/*

# Mono 6.12 (mono-project.com's last release; its focal packages install on 24.04).
RUN gpg --homedir /tmp --no-default-keyring --keyring /usr/share/keyrings/mono.gpg \
      --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys 3FA7E0328081BFF6A14DA29AA6A19B38D3D831EF \
    && echo "deb [signed-by=/usr/share/keyrings/mono.gpg] https://download.mono-project.com/repo/ubuntu stable-focal main" \
      > /etc/apt/sources.list.d/mono.list \
    && apt-get update && apt-get install -y --no-install-recommends mono-complete \
    && rm -rf /var/lib/apt/lists/* && mono --version | head -1

# .NET SDK 10 and the 8.0 runtimes.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /opt/dotnet \
    && bash /tmp/dotnet-install.sh --channel 8.0 --runtime aspnetcore --install-dir /opt/dotnet \
    && rm /tmp/dotnet-install.sh && dotnet --info | head -20

# Go, latest stable.
RUN arch="$(dpkg --print-architecture)" \
    && ver="$(curl -fsSL 'https://go.dev/VERSION?m=text' | head -1)" \
    && curl -fsSL "https://go.dev/dl/${ver}.linux-${arch}.tar.gz" | tar -xz -C /opt && go version

# Rust stable (tests) and nightly (cargo-fuzz).
RUN curl -fsSL https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable \
    && rustup toolchain install nightly --profile minimal \
    && cargo install --locked cargo-fuzz && rustc --version

# Node.js 24.
RUN arch="$(dpkg --print-architecture | sed 's/amd64/x64/')" \
    && ver="$(curl -fsSL https://nodejs.org/dist/index.json | python3 -c 'import json,sys; print(next(r["version"] for r in json.load(sys.stdin) if r["version"].startswith("v24.")))')" \
    && mkdir -p /opt/node && curl -fsSL "https://nodejs.org/dist/${ver}/node-${ver}-linux-${arch}.tar.xz" \
      | tar -xJ -C /opt/node --strip-components=1 && node --version

# Scanner contract: the checkout lives at /src.
COPY . /src
WORKDIR /src

# .NET: restore and build everything (Debug keeps full symbols), plus the netstandard2.1 build of Nino.Core that
# Unity and Mono load.
RUN dotnet build Nino.slnx -c Debug \
    && dotnet build tests/Nino.UnitTests -c Debug -f net8.0 -p:NinoCoreTfm=netstandard2.1 \
    && dotnet build tests/Nino.RuntimeTests -c Debug -f net472

# Go, Rust and TypeScript: fetch and compile everything the tests and fuzzers need.
RUN cd runtimes/go && go vet ./... && go test -count=1 -run '^$' ./...
RUN cd runtimes/rust && cargo fetch && cargo test --workspace --all-features --no-run \
    && (cd fuzz && cargo +nightly fuzz build || echo "WARNING: cargo fuzz build failed")
RUN cd runtimes/typescript && npm ci && npm run build

# Cheap checks that the image works; failures are reported, not fatal.
RUN dotnet test tests/Nino.UnitTests -c Debug --no-build -f net10.0 --filter 'FullyQualifiedName~Untrusted|FullyQualifiedName~HashFlooding|FullyQualifiedName~Strict' \
      || echo "WARNING: .NET security tests reported failures"
RUN mono tests/Nino.RuntimeTests/bin/Debug/net472/Nino.RuntimeTests.exe \
      || echo "WARNING: Nino.RuntimeTests on Mono 6.12 reported failures"
RUN cd runtimes/go && (go test ./... || echo "WARNING: go test reported failures")
RUN cd runtimes/rust && (cargo test --workspace --all-features || echo "WARNING: cargo test reported failures")
RUN cd runtimes/typescript && (npm run test:node || echo "WARNING: npm test reported failures")
