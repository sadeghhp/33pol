One file per receiver named in the Alertmanager config, holding that receiver's webhook URL on a
single line: `gateway-owner.url`, `team-easy.url`. The `*.url` files are not committed.

```bash
printf '%s' 'https://hooks.example.com/services/...' > gateway-owner.url
```

Alertmanager reads the file each time it notifies, so a changed URL needs no restart.
