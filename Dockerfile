# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Build
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
RUN dotnet restore src/Marketing.API/Marketing.API.csproj

COPY src/ src/

# No --no-restore here: the copy above may have introduced a project file change
# the restore layer did not see, and a stale restore fails in a confusing way.
RUN dotnet publish src/Marketing.API/Marketing.API.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# ─────────────────────────────────────────────────────────────────────────────
# Migrations
# ─────────────────────────────────────────────────────────────────────────────
# A stage rather than a separate image, so migrations always run against exactly
# the source that produced the running API. `dotnet ef` needs the SDK and the
# project files, which the runtime image deliberately does not carry.
FROM build AS migrations
WORKDIR /src

# The tool version is pinned in the manifest, so this stage and a developer's
# machine always run the same dotnet-ef. Copied rather than installed globally
# for that reason - a floating version is how a migration works locally and
# fails in CI.
COPY dotnet-tools.json ./
RUN dotnet tool restore

ENTRYPOINT ["dotnet", "ef"]
CMD ["database", "update", \
     "--project", "src/Marketing.DataAccess", \
     "--startup-project", "src/Marketing.API", \
     "--context", "ApplicationDbContext"]

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
USER $APP_UID

EXPOSE 8080

# Liveness only, deliberately. /health/ready reports the database and the broker,
# so using it here would have Docker restart a healthy API because Postgres was
# briefly slow - turning a dependency blip into an outage of its own.
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Marketing.API.dll"]
