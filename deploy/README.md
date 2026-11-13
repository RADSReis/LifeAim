# Deployment

LifeAim runs on the shared VPS, behind its Caddy proxy ([RADSReis/vps-infra](https://github.com/RADSReis/vps-infra)), so it reuses `questkeep.cloud`.

| Branch    | Environment | URL                                     | Image tag |
|-----------|-------------|-----------------------------------------|-----------|
| `develop` | dev         | https://dev-lifeaim-api.questkeep.cloud | `develop` |
| `main`    | prod        | https://lifeaim-api.questkeep.cloud     | `main`    |

```
Internet ─► questkeep-proxy-caddy ─┬─► questkeep-*  (Questkeep)
            (edge network)         └─► lifeaim-{dev,prod}-api :8080
```

## Server layout

```
/srv/lifeaim/                    (owned by the deploy user)
├── dev/    compose.yml, .env    (from deploy/compose.yml)
└── prod/
/srv/questkeep/proxy/sites/
├── lifeaim-dev.caddy            (from deploy/caddy/dev.caddy)
└── lifeaim-prod.caddy
```

`/srv/lifeaim` is created once by an admin, since the deploy user has no sudo:
`sudo mkdir -p /srv/lifeaim && sudo chown <deploy user>: /srv/lifeaim`. The workflow then creates each
environment folder and its first `.env`; secrets added there by hand (the Discord token, ...) are kept.

The proxy's Caddyfile imports `sites/*.caddy`, so LifeAim only adds its own site file and
runs `caddy reload`. A broken file is rejected and the old config keeps serving Questkeep.

## DNS

Each host needs an `A` record pointing to the VPS (the same IP as `questkeep.cloud`) in the `questkeep.cloud` zone:
`dev-lifeaim-api` and `lifeaim-api`. Caddy obtains the certificates by itself once the record exists.

## Continuous deployment

`.github/workflows/deploy.yml` runs on pushes to `develop` and `main`: it runs the tests, builds the
image, pushes it to GHCR (`ghcr.io/radsreis/lifeaim-api`), restarts the API over SSH as the deploy
user and publishes the Caddy site file.

Repository secrets (the same values as in the Questkeep repository): `VPS_HOST`, `VPS_USER` (the deploy user),
`VPS_SSH_KEY` (the deploy user's private key) and `VPS_KNOWN_HOSTS` (the server's host key line).
