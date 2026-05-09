# lxc-163 Lidarr ops scripts

Iteration helpers for the Lidarr fork running in lxc container 163 on
host `diligence`. Everything is plain bash + curl + jq + ssh; no deps
to install.

## Setup

Override host/container/URL via env vars if your setup differs:

```sh
export HOST=diligence    # SSH host running Proxmox
export CT=163            # LXC container ID
export LIDARR_URL=https://lidarr
```

The API key is fetched once from `/var/lib/lidarr/config.xml` on the
container and cached at `~/.cache/lidarr-apikey` (mode 600).

## Scripts

### `lidarr-deploy`
Push the current branch, trigger `lidarr_build.sh` on the container,
filter the noisy MSBuild output down to errors / warnings / milestones,
verify the deployed commit matches local HEAD, wait for the API to come
up, and surface recent startup warnings.

```sh
./scripts/lidarr-deploy
```

Full build log is saved to `/tmp/lidarr-deploy-<timestamp>.log` for
diagnostic spelunking.

### `lidarr-log`
Tail or grep the container log without the ssh-quoting nightmare.

```sh
./scripts/lidarr-log              # last 50 lines
./scripts/lidarr-log -n 200       # last 200 lines
./scripts/lidarr-log -e           # errors/warnings only
./scripts/lidarr-log -f           # follow new lines
./scripts/lidarr-log -f -e        # follow, errors/warnings only
./scripts/lidarr-log -g Identif   # last 50 matching "Identif"
```

Severity is colorized (Error red, Warn yellow, Debug/Trace dim).

### `lidarr`
Thin wrapper around the HTTP API.

```sh
./scripts/lidarr summary          # one-shot dashboard (start here)
./scripts/lidarr queue            # counts by status
./scripts/lidarr queue-list 30    # first 30 items, status + title
./scripts/lidarr dups             # queue grouped by downloadId — find duplication
./scripts/lidarr rejections       # most-common normalized rejection reasons
./scripts/lidarr stuck            # warning/error items, oldest first
./scripts/lidarr history 20       # last 20 events
./scripts/lidarr imports 10       # last 10 successful imports
./scripts/lidarr imports-rate     # per-minute import counts for the last hour
./scripts/lidarr grabs 10         # last 10 release grabs
./scripts/lidarr status           # version / build time
./scripts/lidarr refresh          # POST RefreshMonitoredDownloads
./scripts/lidarr scan             # POST RescanFolders
./scripts/lidarr command Backup   # arbitrary command by name
./scripts/lidarr raw '/queue?page=2'   # raw curl pass-through
```

`summary` is the one to memorize — single command that surfaces queue state,
top duplicating downloads, recent import throughput, and ranked rejections.
`dups` complements it when you want to see *which* downloads are spawning
the most queue records.

### `lidarr-shell`
```sh
./scripts/lidarr-shell                  # interactive shell on container
./scripts/lidarr-shell tail -f /var/log/lidarr.txt   # one-off command
```

## Typical iteration loop

```sh
# edit, commit
./scripts/lidarr-deploy
./scripts/lidarr-log -f -e   # watch for runtime errors
./scripts/lidarr queue       # verify queue is processing
./scripts/lidarr rejections  # see what's still being blocked
```
