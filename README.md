# Monitor

A cross-platform, single-binary hardware dashboard. It runs a small Kestrel web server and serves
a striking, touch-friendly dark dashboard of live system metrics — CPU, memory, network, disk and
temperatures — designed to fill a **1280x400** mini-rack touchscreen, but usable from any browser.

Other machines on the network can push their own stats to the hub, and you can tap to switch which
host you're viewing.

![michka dashboard on the rack panel](docs/dashboard.png)

## Features

- **One self-contained binary** per platform (no .NET install needed on the target). Linux-first,
  Windows code paths included.
- **Live dashboard** over Server-Sent Events: CPU gauge + per-core, memory, network throughput,
  disk usage, temperatures, load average.
- **Multi-host**: lightweight agent mode pushes metrics from other machines; touch tabs to switch.
- **History** stored in embedded SQLite, so trends survive restarts.
- Built with .NET 10, ASP.NET Core Minimal APIs, vanilla JS + Apache ECharts (embedded, offline).

## Quick start

```bash
# Server / hub (UI + local metrics), default port 5000
./michka_s

# Client / agent on another machine, pushing to the hub
./michka_c --hub http://IPAddress:5000 --name my-server
```

`michka_s` defaults to hub mode and `michka_c` to agent mode (chosen from the binary name); both come
from one codebase. Then open `http://<hub-host>:5000`. Settings live on the device in `michka.conf`
(created on first run) and are edited from the full-screen Settings page (swipe left on the touch UI).

## CLI

| Flag | Default | Description |
|------|---------|-------------|
| `--agent` | by name | Force agent mode (push only, no UI). |
| `--hub <url>` | — | Hub URL to push to (agent mode). |
| `--name <n>` | machine name | Host label reported to the hub. |
| `--port <n>` | `michka.conf` / `5000` | Hub HTTP port (overrides the saved config). |
| `--interval <ms>` | `1000` | Sampling/push interval. |
| `--no-local` | off | Hub: don't collect the local machine. |
| `--db <path>` | `monitor.db` | SQLite history file (hub). |

## Build

```bash
# server + client, name set via AssemblyName
dotnet publish src/Monitor/Monitor.csproj -c Release -r linux-x64 -p:AssemblyName=michka_s -o publish/michka_s-linux
dotnet publish src/Monitor/Monitor.csproj -c Release -r linux-x64 -p:AssemblyName=michka_c -o publish/michka_c-linux
dotnet publish src/Monitor/Monitor.csproj -c Release -r win-x64   -p:AssemblyName=michka_c -o publish/michka_c-win
```

