# HyperTizen + HyperHDR on TrueNAS

This deployment runs HyperHDR and the HyperTizen full-frame Plex source bridge
as one TrueNAS Custom App. The published single-container image works with the
form-based installer and `compose.yaml`. The Compose deployment pulls the
ready-made GHCR image, so TrueNAS does not need a local build context. Neither
mode replaces the existing Hyperion app during initial validation.

## Layout

- HyperHDR web UI: `http://192.168.10.10:8091`
- HyperHDR FlatBuffers inside the app: `hyperhdr:19400`
- Source bridge control API: `http://192.168.10.10:19545`
- Unified controls: `http://192.168.10.10:19545/controls.html`
- Server logs: `http://192.168.10.10:19545/logs.html`
- Persistent configuration: `/mnt/Vault/apps/hypertizen-hyperhdr/config`
- Persistent bridge log: `/mnt/Vault/apps/hypertizen-hyperhdr/bridge-state`
- Plex media: `/mnt/Vault/Media` mounted read-only as `/media`
- Intel VAAPI render device: `/dev/dri/renderD128`

HyperHDR is pinned to stable `v21.0.0.0`. The official Debian package is
downloaded during the image build and verified with SHA-256. The bridge image
contains FFmpeg, the Intel media VAAPI driver, and the Python adapter.

## TrueNAS form installation

Use repository `ghcr.io/bigsant/hypertizen-hyperhdr` and tag `latest`. Configure
the following host ports and container ports:

- `8091` -> `8090` TCP (web UI)
- `8093` -> `8092` TCP (secure web UI, optional)
- `19401` -> `19400` TCP (FlatBuffers, optional LAN access)
- `19446` -> `19444` TCP (JSON API)
- `19545` -> `19445` TCP (source bridge control)

Add these host-path mounts:

- `/mnt/Vault/apps/hypertizen-hyperhdr/config` -> `/config`
- `/mnt/Vault/apps/hypertizen-hyperhdr/bridge-state` -> `/state`
- `/mnt/Vault/Media` -> `/media`, read-only

Select **Passthrough available (non-NVIDIA) GPUs**, use restart policy
`Unless Stopped`, and do not enable privileged mode.

Confirm that TrueNAS exposes `/dev/dri/renderD128` to the app. If this node
   does not exist, stop the app and diagnose the Intel iGPU before starting the
   bridge.
Open the HyperHDR UI on port 8091 and import or recreate the LED layout. Set
`controls.html` Source Bridge URL to
   `http://192.168.10.10:19545`, then press Start.

## Building the image yourself

Run this from the root of the extracted build-context archive (not from the
folder containing only the Dockerfile):

```bash
docker build -f deploy/truenas-hyperhdr/Dockerfile \
  -t YOUR_REGISTRY/hypertizen-hyperhdr:latest .
docker push YOUR_REGISTRY/hypertizen-hyperhdr:latest
```

The repository layout is part of the Docker build context because the image
copies the bridge scripts from `tools/`.

The bridge supervisor starts automatically but video decoding does not. A
decode process starts only after the Start request and stops with the Stop
request. This keeps Plex playback unaffected while HyperHDR is being set up.
The controls page can change `sync-lead` from 0 to 5 seconds. The setting is
stored as `/state/bridge-settings.json`; saving it while the bridge is running
restarts only the bridge so the new value takes effect immediately.

The bridge keeps only the latest decoded frame, checks sustained timeline drift
against Plex, and tolerates short status interruptions. Inspect `Bridge timing`
entries in `/state/source-bridge.log`: short drift around 1 s is normal for
Plex's stepped timeline; sustained drift above 2 s triggers a resync, while
`stale-dropped` may increase during a short overload without adding lasting
latency.

## Rollback

Stop the `hypertizen-hyperhdr` app and start the old Hyperion app. The two
configuration directories are separate, and the media dataset is never
writable from the bridge container.
