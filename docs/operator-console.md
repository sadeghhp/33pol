# Operator Console

In-process Spectre.Console TUI for local operations. HTTP admin APIs remain the canonical automation surface.

## Enable

```json
"Gateway": {
  "OperatorConsole": {
    "Enabled": true,
    "RefreshIntervalMs": 1000
  }
}
```

Default: **off** in Production and Docker; **on** in Development sample.

## Commands

| Command | Action |
|---------|--------|
| `help` | List commands |
| `summary` | Metrics snapshot |
| `watch summary` | Refreshing summary |
| `backends` | Model registry + health |
| `requests [--limit N]` | Recent requests |
| `reload` | Config reload |
| `models list` | List models |
| `exit` | Stop console loop |

Commands delegate to `IControlPlaneCommands` (same as HTTP admin).

## Security

Requires admin API key configuration at bootstrap. No secrets are printed to the terminal.
