Title: Add HLTV Commands update bot

## Summary
- Add a Camoufox-based updater that scrapes HLTV team rankings and refreshes the existing 40 teams in `Commands.txt`.
- Add a scheduled and manually triggered GitHub Actions workflow that runs the updater and opens a PR when commands change.
- Add dynamic-proxy configuration so the workflow can route Camoufox through a rotating local proxy, while still allowing `HLTV_PROXY` override.

## Test
- `python -m py_compile scripts/update_hltv_commands.py`
- `python scripts/update_hltv_commands.py --commands Commands_new`

## Notes
- Local smoke test successfully reached HLTV and wrote a temporary `Commands_new` file.
- The updater keeps current team names, logo aliases, team order, and only replaces player names for the existing 40 teams.
