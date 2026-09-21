#!/usr/bin/env bash
#
# Packages the mod the way Vintage Story wants it: modinfo.json and the assembly
# at the root of a zip named <modid>_<version>.zip. That is the layout the mod
# database expects on upload, and the same file a server can be handed directly.
#
# One assembly, two archives. The mod runs on both sides and decides for itself
# which half to start, so the code is the same either way; what differs is that a
# server is handed the map service and a client has no use for a megabyte of it.
#
#   ./package.sh                        a server archive, into dist/
#   ./package.sh --target client        the same mod without the map service
#   ./package.sh --target universal     one archive carrying both services
#   ./package.sh --target windows       a server archive for a Windows host
#   ./package.sh --install DIR          also copy it into a Mods folder
#   ./package.sh --no-build             package whatever was built last
#   ./package.sh --service FILE         use this Linux map service binary
#   ./package.sh --service-win FILE     use this Windows map service binary
#   ./package.sh --no-service           a server archive without one
#   ./package.sh --notices FILE         use this third-party notice file
#   ./package.sh --service-repo DIR     where the map service source lives
#
# Each archive but the server one carries a suffix — _client, _universal,
# _windows — so that all of them can sit in dist/ at once rather than one quietly
# overwriting the next.
#
# A target says which map services travel with the mod. The assembly is the same
# in every archive; a server is handed the service for the machine it runs on, a
# universal archive carries both, and a client has no use for either.
#
# Both halves are built here, because they are one release. The map service is a
# separate program in a separate repository, so where that repository is has to be
# said: `WITCHLIGHT_SERVICE_REPO`, in the environment or in a `.env` file beside
# this script. That file is not committed — a path on one machine is not a fact
# about the project — and `.env.example` is what it should look like.
#
# Building it here is what makes "one archive, one release" true of the build and
# not only of the version check below. The check catches a version bumped without
# a rebuild; nothing could catch a source file edited without one, so the rebuild
# is no longer something to remember.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Where this machine keeps things, which is nobody else's business and so is not
# in the repository. Read before the variables below are settled, and only into
# names nothing has already set — so a one-off `WITCHLIGHT_SERVICE_REPO=... ./package.sh`
# wins over the file, which is the way round every other tool reads one.
env_file="$here/.env"
if [ -f "$env_file" ]; then
    while IFS='=' read -r key value || [ -n "$key" ]; do
        # Whitespace and quotes are how people write these, and neither is part
        # of the name or the value.
        key="${key#"${key%%[![:space:]]*}"}"; key="${key%"${key##*[![:space:]]}"}"
        value="${value#"${value%%[![:space:]]*}"}"; value="${value%"${value##*[![:space:]]}"}"
        value="${value%\"}"; value="${value#\"}"
        value="${value%\'}"; value="${value#\'}"

        # Anything that is not a name and a value is a comment, a blank line, or
        # a mistake, and none of the three is worth stopping the packaging for —
        # this file is a convenience, not a manifest. A name that is not a shell
        # name is skipped rather than assigned, because `printf -v` would fail on
        # it and take the whole run down with it.
        case "$key" in
            ''|'#'*) continue ;;
            *[!A-Za-z0-9_]*|[0-9]*) continue ;;
        esac

        # Only into names nothing has already set, so the environment wins.
        [ -n "${!key-}" ] || printf -v "$key" '%s' "$value"
        export "$key"
    done < "$env_file"
fi

project="$here/Witchlight"
modinfo="$project/modinfo.json"
out="$here/dist"
build=1
install_to=""
service="${WITCHLIGHT_SERVICE:-}"
service_win="${WITCHLIGHT_SERVICE_WIN:-}"
notices="${WITCHLIGHT_NOTICES:-}"
# The map service's own repository, so this script can build it as well as bundle
# it. Empty means nothing said, which is an error only when a service is wanted
# and no binary was named outright.
service_repo="${WITCHLIGHT_SERVICE_REPO:-}"
want_service=1
target=server

# What the map service's binary is called once it is built.
service_name=witchlight

# Where each platform's binary sits inside the archive. The mod reads these same
# two paths; see BundledService.cs.
linux_at="service/linux-x64/witchlight"
windows_at="service/win-x64/witchlight.exe"

while [ $# -gt 0 ]; do
    case "$1" in
        --install) install_to="${2:?--install needs a directory}"; shift 2 ;;
        --out)     out="${2:?--out needs a directory}"; shift 2 ;;
        --no-build) build=0; shift ;;
        --service) service="${2:?--service needs a file}"; shift 2 ;;
        --service-win) service_win="${2:?--service-win needs a file}"; shift 2 ;;
        --service-repo) service_repo="${2:?--service-repo needs a directory}"; shift 2 ;;
        --notices) notices="${2:?--notices needs a file}"; shift 2 ;;
        --no-service) want_service=0; shift ;;
        --target)  target="${2:?--target needs client or server}"; shift 2 ;;
        # Everything the header says, however long it grows: a line range here
        # goes stale the first time a flag is added and says nothing about it.
        -h|--help) awk 'NR>1 && /^#/ { sub(/^# ?/, ""); print; next } NR>1 { exit }' \
                       "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "package: unknown argument $1" >&2; exit 2 ;;
    esac
done

# Which platforms' services this target carries. A target is the whole of the
# decision, so nothing else has to be remembered alongside it.
want_linux=0
want_windows=0
case "$target" in
    server)    want_linux=1 ;;
    windows)   want_windows=1 ;;
    universal) want_linux=1; want_windows=1 ;;
    # A client has nothing to serve.
    client)    want_service=0 ;;
    *) echo "package: --target takes client, server, windows or universal, not $target" >&2
       exit 2 ;;
esac

if [ "$want_service" -eq 0 ]; then
    want_linux=0
    want_windows=0
fi

# The mod's own metadata is the single source of truth for what this is called.
modid=$(jq -r '.modid' "$modinfo")
version=$(jq -r '.version' "$modinfo")
side=$(jq -r '.side' "$modinfo")
[ "$modid" != "null" ] && [ "$version" != "null" ] || {
    echo "package: $modinfo needs a modid and a version" >&2
    exit 1
}

# The map service's repository, said or refused. A path on one machine is not a
# fact about the project, so it is never guessed at: `.env` is where it belongs,
# and the message says so rather than leaving somebody to find that out.
needs_repo() {
    [ -n "$service_repo" ] || {
        echo "package: nothing says where the map service is." >&2
        echo "  Put its repository in $env_file:" >&2
        echo "    WITCHLIGHT_SERVICE_REPO=/path/to/rust/witchlight" >&2
        echo "  or pass --service-repo DIR, or name a built binary with --service FILE," >&2
        echo "  or package without one with --no-service. See .env.example." >&2
        exit 1
    }
    [ -f "$service_repo/Cargo.toml" ] || {
        echo "package: $service_repo is not the map service's repository" >&2
        echo "  (no Cargo.toml in it)" >&2
        exit 1
    }
}

# Where cargo actually put it — asked of cargo rather than worked out from the
# repository's layout. `CARGO_TARGET_DIR`, a `.cargo/config.toml` and a `target`
# symlink can each send the output somewhere else, and a path assembled here
# would be a second opinion on a question cargo already answers.
service_binary() {
    local target_dir
    target_dir=$(cargo metadata --format-version 1 --no-deps \
        --manifest-path "$service_repo/Cargo.toml" 2>/dev/null | jq -r '.target_directory')
    [ -n "$target_dir" ] && [ "$target_dir" != "null" ] || {
        echo "package: cargo could not say where it builds $service_repo" >&2
        exit 1
    }
    echo "$target_dir/release/$service_name"
}

if [ "$build" -eq 1 ]; then
    # Quiet while it works, and the whole log the moment it does not.
    log=$(mktemp)

    # The map service first, because it is the half that takes twenty seconds and
    # the half whose failure is worth seeing before anything else has happened.
    # Skipped for a client archive, which carries none of it.
    if [ "$want_linux" -eq 1 ] && [ -z "$service" ]; then
        needs_repo
        if ! cargo build --release --manifest-path "$service_repo/Cargo.toml" \
                > "$log" 2>&1; then
            cat "$log" >&2
            rm -f "$log"
            echo "package: the map service did not build" >&2
            exit 1
        fi
    fi

    if ! dotnet build "$project/Witchlight.csproj" -c Release --nologo -v quiet > "$log" 2>&1; then
        cat "$log" >&2
        rm -f "$log"
        echo "package: build failed" >&2
        exit 1
    fi
    rm -f "$log"
fi

release="$project/bin/Release"
assembly="$release/Witchlight.dll"
[ -f "$assembly" ] || {
    echo "package: $assembly is missing — build first, or drop --no-build" >&2
    exit 1
}

# A mod that cannot start the map is the thing this is meant to prevent, so a
# missing service stops the packaging rather than shipping quietly without one.
if [ "$want_linux" -eq 1 ] && [ -z "$service" ]; then
    needs_repo
    service=$(service_binary)
fi

if [ "$want_linux" -eq 1 ] && [ ! -f "$service" ]; then
    echo "package: no map service binary at $service." >&2
    echo "  Drop --no-build and it is built from $service_repo," >&2
    echo "  or name one outright with --service FILE," >&2
    echo "  or package without it with --no-service." >&2
    exit 1
fi

# A Windows binary is built on a Windows machine, which this is not, so it is
# never built here and must be named. The release workflow builds it on a
# Windows runner and passes it in.
if [ "$want_windows" -eq 1 ] && [ ! -f "${service_win:-}" ]; then
    echo "package: --target $target needs a Windows map service binary." >&2
    echo "  Name one with --service-win FILE. It is not built here: it is built" >&2
    echo "  on a Windows machine, which the release workflow does." >&2
    exit 1
fi

# The two halves ship as one archive and are one release, so they carry one
# version. Checked against the binary itself rather than against a second file
# saying what it should be: the failure this catches is a mod rebuilt while the
# service was not, which leaves a map whose page reports the version before last
# — and, because the viewer's assets used to be addressed by that number, a
# browser that never fetched the new ones at all.
if [ "$want_linux" -eq 1 ]; then
    built=$("$service" --version 2>/dev/null | awk '{print $NF}')
    if [ "$built" != "$version" ]; then
        echo "package: the map service is $built and the mod is $version." >&2
        echo "  They ship as one archive and must carry one version." >&2
        echo "  Set version in $modinfo and in the service's Cargo.toml to match," >&2
        echo "  then rebuild the service with: cargo build --release" >&2
        echo "  service: $service" >&2
        exit 1
    fi
fi

# The Windows binary is checked by its name rather than by running it, because
# this machine cannot run it. The workflow that builds it names it after the
# version it was built from.
if [ "$want_windows" -eq 1 ]; then
    case "$service_win" in
        *"$version"*) ;;
        *) echo "package: $service_win does not name version $version." >&2
           echo "  Both halves ship as one archive and must carry one version." >&2
           exit 1 ;;
    esac
fi

# Every permissive licence in the service binary asks the same thing of a copy:
# that the notice travel with it. Shipping the binary without one is the failure
# this refuses to make quietly, so a missing notice stops the packaging exactly
# as a missing binary does.
#
# It lives with the source rather than beside the binary, which is why it is
# looked for separately: a binary can be named with --service from anywhere, and
# the notice always belongs to the repository that compiled it.
if [ "$want_service" -eq 1 ] && [ -z "$notices" ] && [ -n "$service_repo" ]; then
    notices="$service_repo/THIRD-PARTY.md"
fi

if [ "$want_service" -eq 1 ] && [ ! -f "${notices:-}" ] \
   && { [ "$want_linux" -eq 1 ] || [ "$want_windows" -eq 1 ]; }; then
    echo "package: no third-party notice found for the map service. Generate it with" >&2
    echo "  ./licenses.py > THIRD-PARTY.md   (in the map service repository)" >&2
    echo "or name one with --notices FILE." >&2
    [ -n "$service_repo" ] && echo "Looked in: $service_repo/THIRD-PARTY.md" >&2
    exit 1
fi

[ -f "$here/LICENSE" ] || {
    echo "package: $here/LICENSE is missing" >&2
    exit 1
}

[ "$want_linux" -eq 1 ]   || service=""
[ "$want_windows" -eq 1 ] || service_win=""
[ "$want_service" -eq 1 ] || notices=""

mkdir -p "$out"
suffix=""
[ "$target" = "client" ]    && suffix="_client"
[ "$target" = "universal" ] && suffix="_universal"
[ "$target" = "windows" ]   && suffix="_windows"
archive="$out/${modid}_${version}${suffix}.zip"

# Everything the game reads, at the root of the zip. Optional pieces are included
# when they exist so adding an icon or assets later needs no change here.
python3 - "$archive" "$modinfo" "$assembly" "$project" "$service" "$here/LICENSE" \
         "$notices" "$service_win" "$linux_at" "$windows_at" <<'PY'
import pathlib, stat, sys, zipfile

archive, modinfo, assembly, project = (pathlib.Path(p) for p in sys.argv[1:5])
service, licence, notices = sys.argv[5], pathlib.Path(sys.argv[6]), sys.argv[7]
service_win, linux_at, windows_at = sys.argv[8], sys.argv[9], sys.argv[10]
included = []

# Where the mod looks for each one. BundledService.cs reads these same paths and
# picks the one matching the machine it starts on.
services = [(service, linux_at, True), (service_win, windows_at, False)]


def write_service(zip_file, source, name, executable):
    """Writes a binary into the archive, keeping the execute bit on the ones
    that need one.

    A zip records permissions only if they are set on the entry, and the mod
    sets them again when it unpacks. Setting them here means the binary is also
    runnable to anyone who opens the archive by hand."""
    info = zipfile.ZipInfo.from_file(source, name)
    info.compress_type = zipfile.ZIP_DEFLATED
    if executable:
        info.external_attr = (stat.S_IFREG | 0o755) << 16
    with open(source, "rb") as handle:
        zip_file.writestr(info, handle.read())


with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zip_file:
    for path in (modinfo, assembly):
        zip_file.write(path, path.name)
        included.append(path.name)

    # The mod's own terms, in every archive: the assembly is this project's code
    # whether or not a map service travels with it.
    zip_file.write(licence, licence.name)
    included.append(licence.name)

    carried = False
    for source, name, executable in services:
        if source:
            write_service(zip_file, source, name, executable)
            included.append(name)
            carried = True

    if carried:
        # Only where a binary is. A client archive links none of it and would be
        # claiming to carry code it does not.
        zip_file.write(notices, "THIRD-PARTY.md")
        included.append("THIRD-PARTY.md")

    icon = project / "modicon.png"
    if icon.exists():
        zip_file.write(icon, icon.name)
        included.append(icon.name)

    assets = project / "assets"
    for path in sorted(assets.rglob("*")) if assets.is_dir() else []:
        if path.is_file():
            name = str(path.relative_to(project))
            zip_file.write(path, name)
            included.append(name)

print("\n".join(f"  {name}" for name in included))
PY

size=$(stat -c%s "$archive")
# Which services went in, named rather than implied, so the line says what the
# archive actually carries.
carrying="no map service"
if [ "$want_linux" -eq 1 ] && [ "$want_windows" -eq 1 ]; then
    carrying="the Linux and Windows map services"
elif [ "$want_linux" -eq 1 ]; then
    carrying="the Linux map service"
elif [ "$want_windows" -eq 1 ]; then
    carrying="the Windows map service"
fi

echo "packaged $modid $version, target $target ($side), with $carrying, $((size / 1024)) KiB"
echo "  $archive"

if [ -n "$install_to" ]; then
    mkdir -p "$install_to"
    cp "$archive" "$install_to/${modid}.zip"
    echo "installed to $install_to/${modid}.zip"
fi
