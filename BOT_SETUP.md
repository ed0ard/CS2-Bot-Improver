# HLTV Commands PR Bot Setup

This workflow is designed to run in your fork and open pull requests against `ed0ard/CS2-Bot-Improver`.

## 1. Create the fork

1. Fork `https://github.com/ed0ard/CS2-Bot-Improver` to your GitHub account.
2. Push this bot branch to your fork.
3. In your fork, open **Settings > Actions > General** and make sure Actions are enabled.

## 2. Create the bot token

Create a GitHub token and save it in your fork as `BOT_TOKEN`:

1. Go to **Settings > Developer settings > Personal access tokens**.
2. Use a classic token with `public_repo`, or a fine-grained token that can:
   - read/write contents in your fork
   - read/write pull requests for `ed0ard/CS2-Bot-Improver`
3. In your fork, go to **Settings > Secrets and variables > Actions > New repository secret**.
4. Add `BOT_TOKEN` with the token value.

## 3. Optional proxy secret

If you already run `dynamic-proxy` or another proxy, add this optional secret:

- `HLTV_PROXY`: for example `http://127.0.0.1:17285` or `socks5://127.0.0.1:17283`

If `HLTV_PROXY` is not set, the workflow downloads and starts `kbykb/dynamic-proxy` in the runner.

## 4. Run the bot

1. Open **Actions > Update HLTV Commands** in your fork.
2. Click **Run workflow**.
3. Keep defaults unless you want to override:
   - `upstream_repository`: `ed0ard/CS2-Bot-Improver`
   - `upstream_branch`: `main`
   - `proxy`: optional proxy endpoint

The workflow updates `Commands.txt`, pushes `bot/update-hltv-commands` to your fork, then opens or updates a PR against `ed0ard/CS2-Bot-Improver:main`.

## Notes

- The bot only keeps the existing 40 teams from `Commands.txt`.
- Existing team names, logo aliases, team order, and heading format are preserved.
    - Player order is preserved where possible; replaced players fill the positions of removed players.
