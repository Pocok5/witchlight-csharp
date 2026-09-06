# Witchlight (server mod)

A Vintage Story mod that exports live world data and shares player map
markers — the game-side half of Witchlight, a web map for Vintage Story
servers.

This is the mod half of a pair. The map itself is rendered and served by a
separate program, [`witchlight`](https://github.com/Tekunogosu/witchlight),
which takes what this mod produces and serves a browsable, zoomable map over
HTTP. This mod knows the game; that program knows pixels. The two are
developed and released together, in separate repositories for organization
only: **both always carry the same version number**, even when a release
only changes one side.

## Features

- Exports terrain, player positions, and map markers to the renderer in
  near real time — a changed chunk reaches the map within a quarter second.
- Shares every player's map markers with everyone else on the server, as
  temporary in-game waypoints.
- Shares land claims with the web map, including who owns what and what
  permissions apply, without bypassing any privilege the server already
  enforces.
- Builds a usable block-color palette automatically, even on a dedicated
  server with no client assets installed, by asking connected players'
  clients and by shipping the base game's own colors pre-recorded.
- Ships the map renderer inside the mod archive — installing this mod is
  enough to get a running map; no separate install step for the renderer.
- Configurable per-command privilege levels and land claim visibility, via
  the shared `witchlight.conf`.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how any of this works
internally, the full server command reference, and the palette handshake.

## Installing

Install like any Vintage Story mod, **on the server and on clients**: the
server half exports data and manages the map service, while the client half
draws other players' markers and supplies block-color palettes and marker
portraits.

Download the appropriate archive from the
[releases page](https://github.com/Tekunogosu/witchlight-csharp/releases)
(or build one — see below) and drop it into the game's `Mods` folder.
Restart the server (and any client) to load it.

Once the world is up, the mod starts the bundled map service automatically.
Its settings live in `witchlight.conf` in the game's `ModConfig` folder,
written with defaults on first run — see
[ARCHITECTURE.md](ARCHITECTURE.md#the-map-service) for the full reference.

## Building

Requires the .NET 10 SDK, a Vintage Story install, and the
[map service source](https://github.com/Tekunogosu/witchlight) (or a
prebuilt binary for it).

```sh
git clone https://github.com/Tekunogosu/witchlight-csharp.git
cd witchlight-csharp
cp .env.example .env    # point WITCHLIGHT_SERVICE_REPO at the map service checkout
./package.sh
```

This produces a server archive at `dist/witchlight_<version>.zip`, building
the map service from source and bundling it in. Common variations:

```sh
./package.sh --target client                              # client-only archive, no map service
./package.sh --install ~/.config/VintagestoryData/Mods    # build and install in one step
./package.sh --service /path/to/witchlight                # use a prebuilt map service binary
```

See [ARCHITECTURE.md](ARCHITECTURE.md#building-and-packaging) for the full
set of options and how the build enforces the version match between the two
halves.

## License

MIT. See [LICENSE](LICENSE).
