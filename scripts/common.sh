# Shared settings for the testbed scripts. Sourced, never run directly.
#
# The testbed lives off the NVMe, alongside the other Vintage Story testbeds on
# this machine rather than on its own. Both overridable, so a machine that keeps
# things elsewhere is not forced into this layout.

TESTBED="${WITCHLIGHT_TESTBED:-/mnt/media/testbed/witchlight-testing}"
GAME="${VINTAGE_STORY:-$HOME/.local/share/vintagestory}"

SERVER_DATA="$TESTBED/server"
CLIENT_DATA="$TESTBED/client"

# The real install, read from only, to seed the testbed client's settings.
REAL_DATA="${VINTAGE_STORY_DATA:-$HOME/.config/VintagestoryData}"

# The testbed's game port. The default 42420 is held by both live servers, 42450
# by the Underrealm testbed, 42451 by Sated's, 42470 by Haft's and 42471 by
# SmithingPlus's; a testbed sharing any of them dies at socket bind with
# "Address already in use" -- after a startup log long enough to look like it got
# further than it did. Defined here rather than in either script so the server
# that binds it and the client that dials it cannot drift apart.
TESTBED_PORT="${WITCHLIGHT_PORT:-42472}"

# The testbed map's port. Witchlight serves a web map as well as running a mod,
# so it takes a second port, and 8080 is the live map's. Nothing dials this but a
# browser, which is why it is printed on start rather than passed to the client.
TESTBED_MAP_PORT="${WITCHLIGHT_MAP_PORT:-8088}"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

die() { printf 'error: %s\n' "$1" >&2; exit 1; }

require_game() {
	[ -f "$GAME/VintagestoryServer.dll" ] ||
		die "Vintage Story not found at '$GAME'. Set VINTAGE_STORY to the folder holding VintagestoryServer.dll."
}

# Mods installed alongside ours in the testbed, by filename, taken from the real
# install. Empty by default: Witchlight reads blocks and serves a map rather than
# interoperating with other mods, so it has nothing it must be tested against.
# Name filenames here, or in WITCHLIGHT_EXTRA_MODS, to put some in.
TESTBED_EXTRA_MODS="${WITCHLIGHT_EXTRA_MODS:-}"

# Copies the extra mods in if they are not already there. Kept separate from
# install_mod so a wipe restores them without a rebuild being involved.
install_extra_mods() {
	target="$1"
	mkdir -p "$target/Mods"

	for name in $TESTBED_EXTRA_MODS; do
		[ -f "$target/Mods/$name" ] && continue
		if [ -f "$REAL_DATA/Mods/$name" ]; then
			cp "$REAL_DATA/Mods/$name" "$target/Mods/$name"
			printf 'extra:  %s\n' "$name"
		else
			printf 'extra:  %s not found in %s/Mods, skipping\n' "$name" "$REAL_DATA"
		fi
	done
}

# Rebuilds both halves and drops the archive into one data dir's Mods folder.
# Both scripts do this on every launch so there is no separate build step to
# forget.
#
# package.sh is the whole of the build: it compiles the mod, builds the map
# service out of the Rust repository named in .env, refuses a pair whose versions
# disagree, and writes the archive a player would install. Testing that artifact
# rather than an unpacked bin/ folder is the point -- an asset missing from the
# package is invisible until something loads the zip.
#
# It installs as <modid>.zip, overwriting whatever was there. That fixed name
# keeps a version bump from leaving the old build beside the new one, but it
# cannot clear a copy installed under some other name before the convention --
# so the stale ones are swept first.
install_mod() {
	target="$1"
	shift

	mkdir -p "$target/Mods"
	remove_stale_mod_copies "$target"

	( cd "$REPO" && ./package.sh --install "$target/Mods" "$@" >/dev/null ) ||
		die "packaging failed (run ./package.sh to see why)"

	printf 'mod:    %s\n' "$target/Mods/witchlight.zip"
}

# Deletes any copy of this mod from the testbed that is not the one install_mod
# writes, matching on the modid recorded inside each zip rather than on its
# filename, so a versioned copy left by an older build or dropped in by a mod
# manager is caught too.
#
# Two archives sharing a modid is not a warning to live with. Vintage Story logs
# "Multiple mods share the mod ID" and loads only the highest version -- and when
# that resolution fails it loads NEITHER: no client half, no network channel, so
# the mark key does nothing and a palette asked for is never sent. It reads as
# three unrelated bugs rather than as one duplicate file.
#
# witchlight.zip itself is skipped: it is what package.sh is about to overwrite.
# Other mods, witchlightheatmap among them, carry their own modid and are never
# considered.
remove_stale_mod_copies() {
	target="$1"

	for existing in "$target"/Mods/*.zip; do
		[ -e "$existing" ] || continue
		[ "$(basename "$existing")" = "witchlight.zip" ] && continue

		existing_id=$(unzip -p "$existing" modinfo.json 2>/dev/null |
			python3 -c 'import sys,re; m=re.search(r"\"modid\"\s*:\s*\"([^\"]+)\"", sys.stdin.read(), re.I); print(m.group(1) if m else "")' 2>/dev/null)

		[ "$existing_id" = "witchlight" ] || continue
		rm -f "$existing" &&
			printf 'mod:    removed stale %s\n' "$(basename "$existing")"
	done
}
