Title: Add HLTV Commands update bot

## Summary
- Add a Camoufox-based updater that scrapes HLTV team rankings and regenerates the `ADD TEAMS` block in `Commands.txt`.
- Add a scheduled and manually triggered GitHub Actions workflow that runs the updater and opens a PR when commands change.
- Add dynamic-proxy configuration so the workflow can route Camoufox through a rotating local proxy, while still allowing `HLTV_PROXY` override.

## Test
- `python -m py_compile scripts/update_hltv_commands.py`
- `python scripts/update_hltv_commands.py --max-teams 2`

## Notes
- Local smoke test successfully reached HLTV and updated a temporary two-team result.
- The actual workflow runs without `--max-teams`, so it writes every team found on the HLTV ranking page.
