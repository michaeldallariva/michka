# Monitor

A cross-platform, single-binary hardware dashboard. It runs a small Kestrel web server and serves
a striking, touch-friendly dark dashboard of live system metrics — CPU, memory, network, disk and
temperatures — designed to fill a **1280x400** mini-rack touchscreen, but usable from any browser.

Other machines on the network can push their own stats to the hub, and you can tap to switch which
host you're viewing.

<img width="939" height="257" alt="Image" src="https://github.com/user-attachments/assets/09d7833a-c52c-458c-9522-6a70e30ebc99" />
<img width="1454" height="460" alt="Image" src="https://github.com/user-attachments/assets/84dc22f5-c8f4-438f-8d53-cb6fd48bf1a8" />
<img width="1451" height="454" alt="Image" src="https://github.com/user-attachments/assets/18b6955b-9adc-418e-8444-0be10277fad0" />
<img width="1450" height="456" alt="Image" src="https://github.com/user-attachments/assets/6e4686bf-f070-442a-8d21-21aa4d609aa1" />
<img width="1450" height="455" alt="Image" src="https://github.com/user-attachments/assets/e00b3bb3-10a8-4a39-a5d4-df80e4c3ce7c" />

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

