# Xbox 360 GPD catalog

Served to desktop and mobile apps from
`https://raw.githubusercontent.com/Achievement-Labs/achievement-labs/gpd-catalog/`.

- `index.json`: `{ version: 1, generated, titles: { TITLEID: { name, achievements, gamerscore, bytes, sha256 } } }`
- `gpd/TITLEID.gpd`: locked catalog GPDs (achievements, images and strings only; no unlocks, settings or sync history).

Apps download a GPD only when its size and SHA-256 match `index.json`, then insert it locked into the user's profile.
Add or update titles from a `main` checkout with:

```text
node tools/xbox360/gpd-catalog.cjs add PATH_TO_CATALOG_CHECKOUT TITLEID.gpd [...]
```

`verify` runs on every push to this branch.
