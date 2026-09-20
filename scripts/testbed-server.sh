#!/usr/bin/env bash
# Runs the Witchlight testbed server against its own data directory, rebuilding
# and installing both halves first. Runs in the foreground: Ctrl+C stops it.
#
#   testbed-server.sh [start]   build, install, launch  (default)
#   testbed-server.sh wipe      delete the world and its map, keep config and mods
#   testbed-server.sh wipe-all  also delete configs and player data, relaunch
#   testbed-server.sh stop      stop one running in another terminal
#   testbed-server.sh logs      tail the last server log
#   testbed-server.sh map       tail the map service's own log
#   testbed-server.sh status    is a testbed server running?

set -euo pipefail
. "$(dirname "$(readlink -f "$0")")/common.sh"

CONFIG="$SERVER_DATA/serverconfig.json"
MOD_CONFIG="$SERVER_DATA/ModConfig/witchlight.conf"

# What a testbed made from scratch calls its world. An existing one keeps the
# name it was made with -- see world_file, which reads the config rather than
# assuming this.
WORLD="$SERVER_DATA/Saves/witchlight.vcdbs"

# The save the server will actually open, which is whatever serverconfig.json
# names. Only a testbed this script created is called witchlight.vcdbs; one made
# by hand before these scripts existed is called something else, and printing a
# path the server is not opening would be worse than printing none.
world_file() {
	[ -f "$CONFIG" ] || { printf '%s' "$WORLD"; return; }

	python3 - "$CONFIG" "$WORLD" <<-'PY'
	import json, sys
	path, fallback = sys.argv[1], sys.argv[2]
	with open(path) as fh:
	    cfg = json.load(fh)
	print(cfg.get("WorldConfig", {}).get("SaveFileLocation") or fallback)
	PY
}

# The server settings a testbed wants, as opposed to a public server's. Written
# once, on first run; after that the file is the user's to edit.
#
# Nothing here shapes the world: Witchlight draws whatever terrain is there, so
# the testbed generates an ordinary survival world.
seed_config() {
	[ -f "$CONFIG" ] && return 0

	require_game
	mkdir -p "$SERVER_DATA"
	printf 'config: generating %s\n' "$CONFIG"

	( cd "$GAME" && dotnet VintagestoryServer.dll --dataPath "$SERVER_DATA" --genconfig ) >/dev/null

	python3 - "$CONFIG" "$WORLD" "$TESTBED_PORT" <<-'PY'
	import json, sys
	path, world, port = sys.argv[1], sys.argv[2], int(sys.argv[3])
	with open(path) as fh:
	    cfg = json.load(fh)

	if not cfg.get("WorldConfig"):
	    cfg["WorldConfig"] = {}
	wc = cfg["WorldConfig"]
	wc["SaveFileLocation"] = world
	wc["WorldName"] = "witchlight"

	# A testbed is for one person walking around in it, so the gates that exist
	# for public servers only get in the way here.
	cfg["ServerName"] = "Witchlight testbed"
	cfg["Port"] = port
	cfg["WhitelistMode"] = "off"
	cfg["AdvertiseServer"] = False
	cfg["Upnp"] = False
	cfg["PassTimeWhenEmpty"] = False

	with open(path, "w") as fh:
	    json.dump(cfg, fh, indent=1)
	PY
}

# The game port lives in serverconfig.json, which is written once and then
# belongs to the user. A testbed created before the port moved would keep
# dialling the old one forever, so the value is reasserted on every start -- and
# only rewritten when it actually differs, leaving every other edit untouched.
enforce_port() {
	[ -f "$CONFIG" ] || return 0

	python3 - "$CONFIG" "$TESTBED_PORT" <<-'PY' || die "could not check the port in $CONFIG"
	import json, sys
	path, port = sys.argv[1], int(sys.argv[2])
	with open(path) as fh:
	    cfg = json.load(fh)
	if cfg.get("Port") == port:
	    sys.exit(0)
	was = cfg.get("Port")
	cfg["Port"] = port
	with open(path, "w") as fh:
	    json.dump(cfg, fh, indent=1)
	print(f"port:   serverconfig.json {was} -> {port}")
	PY
}

# The map's own port, in witchlight.conf. The same reasoning as the game port
# above, and a second one on top of it: the live map holds 8080, so a testbed
# left on the default serves nothing and the browser quietly shows the live map
# instead -- which reads as the testbed working.
#
# The file is written by the map service on first run, so there is nothing to
# correct until it exists. Only the `bind` line is touched, and only its port, so
# an address deliberately narrowed to loopback stays narrowed.
enforce_map_port() {
	[ -f "$MOD_CONFIG" ] || return 0

	python3 - "$MOD_CONFIG" "$TESTBED_MAP_PORT" <<-'PY' || die "could not check the map port in $MOD_CONFIG"
	import re, sys
	path, port = sys.argv[1], int(sys.argv[2])
	with open(path) as fh:
	    text = fh.read()

	# The key at the start of a line, so a mention of `bind` inside the notes
	# above it is not what gets rewritten.
	found = re.search(r'^(bind\s*=\s*")([^"]*)(")', text, re.M)
	if not found:
	    sys.exit(0)

	was = found.group(2)
	host, _, had = was.rpartition(":")
	if not host:
	    host, had = was, ""
	if had == str(port):
	    sys.exit(0)

	text = text[:found.start(2)] + f"{host}:{port}" + text[found.end(2):]
	with open(path, "w") as fh:
	    fh.write(text)
	print(f"map:    witchlight.conf bind {was} -> {host}:{port}")
	PY
}

# Stopping first matters for the wipe commands: deleting Saves out from under a
# running server leaves it writing into files that no longer exist, and the
# world it then saves on shutdown can land back on disk after the wipe.
stop_running() {
	pids=$(pgrep -f "VintagestoryServer.dll --dataPath $SERVER_DATA" || true)
	[ -z "$pids" ] && return 0

	printf 'note:   stopping testbed server already running (pid %s)\n' "$(echo $pids | tr '\n' ' ')"
	kill $pids 2>/dev/null || true
	for _ in $(seq 1 30); do
		pgrep -f "VintagestoryServer.dll --dataPath $SERVER_DATA" >/dev/null || return 0
		sleep 1
	done
	die "the previous testbed server would not stop; kill it by hand and retry"
}

case "${1:-start}" in
	start) ;;
	wipe|wipe-all) stop_running ;;&
	wipe)
		# The world, and the map drawn from it. Everything that is expensive to
		# put back -- the Mods folder, serverconfig.json, witchlight.conf -- is
		# left alone, because regenerating terrain is the common case and none of
		# that is part of the terrain.
		#
		# The map goes with the world it was drawn from. A map kept across a wipe
		# is tiles of terrain that no longer exists, over a world that has never
		# been walked, and the mismatch reads as the map failing to update.
		rm -rf "$SERVER_DATA/Saves" "$SERVER_DATA/Cache" "$SERVER_DATA/Logs" \
		       "$SERVER_DATA/witchlight"
		printf 'wipe:   world and map deleted, config and mods kept\n'
		;;
	wipe-all)
		# Also the configs. The map service regenerates ModConfig/witchlight.conf
		# at boot, so this is how to get back to the shipped defaults after
		# editing the live one by hand.
		rm -rf "$SERVER_DATA/Saves" "$SERVER_DATA/Cache" "$SERVER_DATA/Logs" \
		       "$SERVER_DATA/witchlight" "$SERVER_DATA/ModConfig" \
		       "$SERVER_DATA/Playerdata" "$SERVER_DATA/serverconfig.json" \
		       "$SERVER_DATA/servermagicnumbers.json"
		printf 'wipe-all: world, map, configs and player data deleted, mods kept\n'
		;;
	logs)
		exec tail -n 200 -f "$SERVER_DATA/Logs/server-main.log"
		;;
	map)
		# The map service logs beside the game's own logs, under its own name.
		exec tail -n 200 -f "$SERVER_DATA/Logs/witchlight-service.log"
		;;
	stop)
		if pids=$(pgrep -f "VintagestoryServer.dll --dataPath $SERVER_DATA"); then
			kill $pids 2>/dev/null || true
			printf 'stop:   signalled %s\n' "$(echo $pids | tr '\n' ' ')"
		else
			printf 'stop:   nothing running\n'
		fi
		exit 0
		;;
	status)
		if pgrep -f "VintagestoryServer.dll --dataPath $SERVER_DATA" >/dev/null; then
			echo "running"
		else
			echo "not running"
		fi
		exit 0
		;;
	*)
		die "usage: $(basename "$0") {start|stop|wipe|wipe-all|logs|map|status}"
		;;
esac

require_game

# "Address already in use" is otherwise the whole error: a server left running
# from an earlier session holds the port and the new one dies at socket bind,
# after a startup log long enough to look like it got further than it did.
stop_running

seed_config
enforce_port
enforce_map_port
install_mod "$SERVER_DATA"
install_extra_mods "$SERVER_DATA"

printf 'data:   %s\n' "$SERVER_DATA"
printf 'world:  %s\n' "$(world_file)"
printf 'port:   %s\n' "$TESTBED_PORT"
printf 'map:    http://localhost:%s/\n' "$TESTBED_MAP_PORT"
printf 'start:  Ctrl+C to stop\n\n'

cd "$GAME"
exec dotnet VintagestoryServer.dll --dataPath "$SERVER_DATA"
