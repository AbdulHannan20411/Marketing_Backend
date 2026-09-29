# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Build
# ─────────────────────────────────────────────────────────────────────────────
# --platform=$BUILDPLATFORM pins the SDK to the *builder's* architecture and
# lets .NET cross-compile to the target with `-a $TARGETARCH`. Without it,
# building an arm64 image on an x64 machine runs the whole SDK under QEMU
# emulation, which turns a two-minute build into twenty.
#
# arm64 matters here because it is what AWS Graviton runs, and Graviton Fargate
# is roughly 20% cheaper than x86 for the same work.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, from nothing but the project files.
#
# This is the whole reason the copy is split in two: a change to a .cs file must
# not invalidate the restore layer. Central package management means the two
# Directory.*.props files are part of the restore input, so they belong here
# too - without them `dotnet restore` cannot resolve a single version.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Marketing.API/Marketing.API.csproj                 src/Marketing.API/
COPY src/Marketing.Application/Marketing.Application.csproj   src/Marketing.Application/
COPY src/Marketing.Business/Marketing.Business.csproj         src/Marketing.Business/
COPY src/Marketing.Common/Marketing.Common.csproj             src/Marketing.Common/
COPY src/Marketing.DataAccess/Marketing.DataAccess.csproj     src/Marketing.DataAccess/
COPY src/Marketing.Infrastructure/Marketing.Infrastructure.csproj src/Marketing.Infrastructure/
COPY src/Marketing.Scheduler/Marketing.Scheduler.csproj       src/Marketing.Scheduler/
COPY src/Marketing.Shared/Marketing.Shared.csproj             src/Marketing.Shared/

# The API's project references pull in the other seven, so restoring it alone
# covers the whole graph without dragging the test projects in.
RUN dotnet restore src/Marketing.API/Marketing.API.csproj -a $TARGETARCH

COPY src/ src/

# No --no-restore here: the copy above may have introduced a project file change
# the restore layer did not see, and a stale restore fails in a confusing way.
RUN dotnet publish src/Marketing.API/Marketing.API.csproj \
    -c Release \
    -a $TARGETARCH \
    -o /app/publish \
    /p:UseAppHost=false

# ─────────────────────────────────────────────────────────────────────────────
# Migration bundle
# ─────────────────────────────────────────────────────────────────────────────
# `dotnet ef migrations bundle` compiles the migrations into a single executable
# that carries no SDK, no source and no project files.
#
# The previous shape of this file shipped the SDK image plus the whole source
# tree as the migration image - about 1.1 GB, pulled by a task that runs for
# four seconds on every deploy. The bundle is ~75 MB on top of a base layer the
# API image already pulled, so in practice the migration step downloads nothing.
#
# It is also strictly safer: the migration binary is built from exactly the
# commit that produced the API image and cannot drift from it, and there is no
# `dotnet ef` version to disagree about.
FROM build AS bundle
ARG TARGETARCH
WORKDIR /src

# The tool version is pinned in the manifest, so this stage and a developer's
# machine always build the bundle with the same dotnet-ef.
COPY dotnet-tools.json ./
RUN dotnet tool restore

# Self-contained, so the bundle has no opinion about which runtime is installed
# where it lands. --force overwrites a stale bundle on a rebuild.
RUN dotnet ef migrations bundle \
    --project src/Marketing.DataAccess \
    --startup-project src/Marketing.API \
    --context ApplicationDbContext \
    --configuration Release \
    --self-contained \
    --runtime linux-$TARGETARCH \
    --output /app/efbundle \
    --force

# ─────────────────────────────────────────────────────────────────────────────
# Runtime
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Two things this image does not have that this application needs.
#
# fonts-liberation: the invoice renderer resolves a TrueType font at render time
# and throws a deliberate, loud error if it finds none. Without this package the
# application starts happily and then fails on the first invoice download - a
# failure that only appears in production, and only for a customer.
#
# ICU is already present in the Debian-based image and is *required*: this
# solution builds with InvariantGlobalization disabled, and country names and
# IANA timezones both read from ICU. See the note on the environment below.
#
# curl: the runtime image ships no HTTP client, and a HEALTHCHECK needs one. It
# is the smallest honest option - the alternative is a health check that cannot
# actually reach the endpoint it claims to test.
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-liberation curl \
    && rm -rf /var/lib/apt/lists/*

# Never set DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 here. It is a common line in
# .NET Dockerfiles and it would silently break two things: every country would
# render as its two-letter code, and every recurring campaign's IANA timezone
# would fail to resolve. It is set to 0 explicitly so that a future edit has to
# be deliberate rather than accidental.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    # Diagnostics off: nothing in the image consumes them and they open a socket.
    DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

# Non-root. APP_UID is defined by the base image (1654); the published output is
# read-only to it, which is what we want - nothing in this application writes to
# its own directory.
#
# On ECS this image can additionally run with readonlyRootFilesystem: true, but
# only when Storage:Provider is S3. With the local provider the application
# creates and writes Storage:RootPath, and a read-only filesystem fails at
# start-up rather than at the first upload.
USER $APP_UID

EXPOSE 8080

# Liveness only, deliberately. /health/ready reports the database and the broker,
# so using it here would have Docker restart a healthy API because Postgres was
# briefly slow - turning a dependency blip into an outage of its own.
#
# ECS ignores this and uses the healthCheck block in the task definition, which
# says the same thing; it is kept for `docker run` and compose.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Marketing.API.dll"]

# ─────────────────────────────────────────────────────────────────────────────
# Migrations
# ─────────────────────────────────────────────────────────────────────────────
# Deliberately built on the same base as the runtime stage. It does not need
# ASP.NET Core, but sharing the layer means a deployment that has already pulled
# the API image downloads only the bundle itself.
#
# Run as a one-off before rolling the API forward:
#   docker compose --profile migrate run --rm migrate
#   aws ecs run-task --task-definition marketing-migrate ...
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS migrations
WORKDIR /app

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

COPY --from=bundle /app/efbundle ./efbundle

# The bundle reads configuration the same way the API does, so a connection
# string supplied as Database__ConnectionString in the environment is picked up
# without this file. It is copied anyway because the bundle also reads defaults
# from it, and a missing appsettings.json turns a clear error into a null
# reference.
COPY --from=build /app/publish/appsettings.json ./appsettings.json

USER $APP_UID

# No arguments: the bundle's default action is to apply every pending migration
# and exit non-zero if it cannot. That exit code is what a deployment pipeline
# should gate the service update on.
ENTRYPOINT ["./efbundle"]
