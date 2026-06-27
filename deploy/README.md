# Deploying the Michka hub on Debian / Linux

The Michka **hub** is a single self-contained binary (the .NET runtime is bundled — no `dotnet`
install needed). This folder builds two ways to deploy it:

| Artifact | For |
|---|---|
| `michka-hub_<ver>_amd64.deb` | Debian/Ubuntu — headless hub, installs + starts automatically |
| `michka-kiosk_<ver>_amd64.deb` | **optional** touchscreen appliance add-on (pulls in cage + Chromium) |
| `michka-agent_<ver>_amd64.deb` | Debian/Ubuntu — the Linux **client** (pushes metrics to a hub) |
| `michka-<ver>-linux-x64.tar.gz` | any systemd Linux — bundles hub + client; `install.sh` does all roles |

Everything installs to predictable locations:

```
/opt/michka/michka_s        the hub binary
/opt/michka/kiosk.sh        kiosk launcher (kiosk only)
/var/lib/michka/            data: michka.conf, michka.db, templates/, widgets/
/etc/systemd/system/        michka-hub.service (+ michka-kiosk.service for the appliance)
```

The hub runs **headless as the unprivileged `michka` user** and is reached from any browser on
your network. Adding the kiosk package turns the machine into a self-contained touchscreen
appliance and (only then) elevates the hub to root so it can drive the local display.

---

## Option A — Debian / Ubuntu (`.deb`, recommended)

### Headless hub

```bash
sudo apt install ./michka-hub_1.0.0_amd64.deb
```

`apt` resolves the (tiny) library dependencies, creates the `michka` user, and enables + starts
`michka-hub`. When it finishes the hub is already live:

```bash
systemctl status michka-hub
# open http://<this-host-ip>:5000/ in a browser
```

> Plain `dpkg -i michka-hub_1.0.0_amd64.deb` also works, but use `apt install ./file.deb` so
> dependencies install automatically.

### Touchscreen appliance (optional kiosk)

On the machine physically wired to the panel, also install the kiosk add-on. It pulls in the
**entire display stack automatically** (cage, Chromium, Mesa, grim, wlr-randr) and starts the
full-screen dashboard on tty1 at boot:

```bash
sudo apt install ./michka-hub_1.0.0_amd64.deb ./michka-kiosk_1.0.0_amd64.deb
```

Installing `michka-kiosk` switches the hub to run as root (so the screensaver screen-power, the
"exit to login", kiosk-restart-on-port-change and kiosk-autostart controls work). Removing it
reverts the hub to the unprivileged user.

### Agent (client) on other machines

On each machine you want to **report into** a hub, install the agent instead of the hub:

```bash
sudo apt install ./michka-agent_1.0.0_amd64.deb
sudoedit /etc/default/michka-agent     # set MICHKA_ARGS="--hub http://<hub-ip>:5000 --name my-box"
sudo systemctl start michka-agent
```

The host then shows up as its own tab on the hub's dashboard. (The agent has no web UI.)

---

## Option B — Any systemd Linux (`tar.gz`)

For non-Debian distros (or if you prefer not to use `dpkg`). The tarball bundles both the hub
(`michka_s`) and the client (`michka_c`); `install.sh` picks the role:

```bash
tar xzf michka-1.0.0-linux-x64.tar.gz
cd michka-1.0.0-linux-x64

# Hub:
sudo ./install.sh                 # headless hub
sudo ./install.sh --kiosk         # hub + kiosk appliance
sudo ./install.sh --port 8080     # serve on a custom port

# Agent (client):
sudo ./install.sh --agent --hub http://<hub-ip>:5000 --name my-box
```

On Debian/Ubuntu `--kiosk` installs the display packages via `apt`. On other distros it prints
the package list for you to install with your own package manager
(`cage chromium libegl1 libgles2 libgl1-mesa-dri grim wlr-randr curl fonts-noto-cjk`).

Uninstall:

```bash
sudo ./uninstall.sh            # keep your data in /var/lib/michka
sudo ./uninstall.sh --purge    # also delete data + the michka user
```

---

## After installing

- **URL:** `http://<host>:5000/` (the port lives in `/var/lib/michka/michka.conf`).
- **Change the port:** edit `/var/lib/michka/michka.conf` (`"port"`) and
  `sudo systemctl restart michka-hub` — or change it in the Settings page (the hub restarts
  itself). With `--port` at install time the port is pinned on the service's `ExecStart`.
- **Firewall:** allow the port on the LAN, e.g. `sudo ufw allow 5000/tcp`.
- **Logs:** `journalctl -u michka-hub -f` (and `-u michka-kiosk` for the display).

### Connect agents (other machines reporting to this hub)

Install the **client** on other hosts (see the agent install above): `michka-agent_<ver>_amd64.deb`
or `./install.sh --agent --hub http://<hub-host>:5000`. New hosts appear as tabs automatically.
The Windows client ships separately with its own GUI/service installer.

### Templates and widgets (drop-ins)

Both are on-disk and need no rebuild — drop folders into the data dir and they appear in Settings
/ the Widgets page:

```
/var/lib/michka/templates/<id>/    UI skins (style.css, script.js, preview.png)
/var/lib/michka/widgets/<id>/      dashboard widgets (widget.js, widget.json, widget.css)
```

The hub seeds each with a `README.txt` describing the format on first run.

---

## Building the packages

Run on a Debian/Ubuntu host (needs `dpkg-deb` and `tar`). It needs both `linux-x64` binaries —
the hub (`michka_s`) and the client (`michka_c`). Provide them one of three ways:

```bash
# 1) let build.sh publish both (needs the .NET SDK + the repo checked out alongside deploy/)
./build.sh

# 2) point it at prebuilt binaries
BINARY_HUB=/path/to/michka_s BINARY_AGENT=/path/to/michka_c ./build.sh

# 3) drop both next to build.sh as ./michka_s and ./michka_c, then run it
cp /path/to/michka_s /path/to/michka_c . && ./build.sh
```

To publish the binaries from the repo on any machine with the .NET SDK:

```bash
dotnet publish src/Monitor/Monitor.csproj -c Release -r linux-x64 -p:AssemblyName=michka_s -o out
dotnet publish src/Monitor/Monitor.csproj -c Release -r linux-x64 -p:AssemblyName=michka_c -o out
```

Artifacts land in `deploy/dist/` with a `SHA256SUMS`. Bump `deploy/VERSION` to change the package
version.

---

## Upgrading

- **`.deb`:** install the newer package over the old one (`sudo apt install ./michka-hub_<new>.deb`);
  the service restarts automatically. Your data in `/var/lib/michka` is untouched.
- **tarball:** re-run `sudo ./install.sh` from the new tarball — it replaces the binary in place
  and restarts the hub.

---

## Notes / troubleshooting

- **Dependencies:** the hub is self-contained, so it only needs base C/C++ runtime libraries
  (`libc6`, `libgcc-s1`, `libstdc++6`, `zlib1g`) present on every Debian system. No `dotnet`, no ICU
  (the build uses invariant globalization).
- **Security:** headless installs run unprivileged. The hub is **HTTP on your LAN** — don't expose
  it directly to the internet; put it behind a reverse proxy / VPN if you need remote access.
- **Kiosk won't start:** `journalctl -u michka-kiosk`. It needs a free tty1 and a working GL/EGL
  stack; back-to-back restarts can wedge Chromium's GL context — a single `systemctl restart
  michka-kiosk` usually fixes it. Chinese text needs `fonts-noto-cjk`.
- **Ubuntu Chromium:** Ubuntu ships Chromium as a snap; the `chromium` dependency may not resolve.
  Install a Wayland-capable Chromium/Chrome and ensure `chromium` or `chromium-browser` is on `PATH`.
- **Architecture:** packages are `amd64` (x86-64). For ARM (e.g. a Pi), publish with
  `-r linux-arm64` and adjust `ARCH`/`-r` accordingly.
