from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from camoufox.sync_api import Camoufox


RANKING_URL = "https://www.hltv.org/ranking/teams"
TEAM_BLOCK_START = "ADD TEAMS"
TEAM_BLOCK_END = "COORDINATED BUY"


@dataclass(frozen=True)
class Team:
    rank: int
    heading: str
    name: str
    players: tuple[str, ...]
    logo: str


@dataclass(frozen=True)
class ExistingTeam:
    heading: str
    name: str
    players: tuple[str, ...]
    logo: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Update Commands.txt team commands from HLTV rankings."
    )
    parser.add_argument("--commands", default="Commands.txt", help="Commands file to update")
    parser.add_argument("--url", default=RANKING_URL, help="HLTV ranking URL")
    parser.add_argument(
        "--proxy",
        default=None,
        help="Proxy server for Camoufox, e.g. http://127.0.0.1:17285 or socks5://127.0.0.1:17283",
    )
    parser.add_argument(
        "--max-teams",
        type=int,
        default=40,
        help="Maximum number of ranked teams to write",
    )
    parser.add_argument(
        "--headless",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="Run Camoufox headless",
    )
    return parser.parse_args()


def normalize_text(value: str) -> str:
    return re.sub(r"\s+", " ", value).strip()


def team_key(team_name: str) -> str:
    value = team_name.lower().replace("&", "and")
    value = re.sub(r"\besports\b|\bgaming\b|\bteam\b|\bclan\b", "", value)
    return re.sub(r"[^a-z0-9]", "", value)


def logo_code(team_name: str) -> str:
    letters = re.sub(r"[^A-Za-z0-9]", "", team_name).lower()
    if not letters:
        return "team"
    if len(letters) <= 4:
        return letters
    return letters[:4]


def extract_rank(text: str) -> int | None:
    match = re.search(r"#(\d+)", text)
    if not match:
        return None
    return int(match.group(1))


def scrape_teams(
    url: str,
    proxy: str | None,
    max_teams: int,
    headless: bool,
    existing_teams: dict[str, ExistingTeam],
) -> list[Team]:
    launch_options = {
        "headless": headless,
        "humanize": True,
        "block_images": False,
        "block_webrtc": True,
        "locale": "en-US",
        "os": ["windows", "macos", "linux"],
    }
    if proxy:
        launch_options["proxy"] = {"server": proxy}
        launch_options["geoip"] = True

    with Camoufox(**launch_options) as browser:
        page = browser.new_page()
        page.goto(url, wait_until="domcontentloaded", timeout=120_000)
        page.wait_for_selector(".ranked-team, .ranking-header", timeout=120_000)
        page.wait_for_timeout(2_000)
        teams = page.eval_on_selector_all(
            ".ranked-team",
            """
            nodes => nodes.map(node => {
              const text = node.innerText || '';
              const nameEl = node.querySelector('.name');
              const playerEls = Array.from(node.querySelectorAll('.playersLine .nick, .nick'));
              const players = playerEls.map(el => (el.textContent || '').trim()).filter(Boolean);
              return {
                text,
                name: nameEl ? nameEl.textContent.trim() : '',
                players,
              };
            })
            """,
        )

    scraped_by_key: dict[str, tuple[str, ...]] = {}
    for item in teams:
        name = normalize_text(item.get("name", ""))
        players = tuple(normalize_text(player) for player in item.get("players", [])[:5])
        if not name or len(players) < 5:
            continue
        scraped_by_key[team_key(name)] = players

    parsed: list[Team] = []
    for index, existing in enumerate(list(existing_teams.values())[:max_teams], start=1):
        scraped_players = scraped_by_key.get(team_key(existing.name))
        ordered_players = (
            align_players(existing.players, scraped_players)
            if scraped_players
            else existing.players
        )
        parsed.append(
            Team(
                rank=index,
                heading=existing.heading,
                name=existing.name,
                players=ordered_players,
                logo=existing.logo,
            )
        )
    return parsed[:max_teams]


def command_line(side: str, team: Team, team_slot: int) -> str:
    add_command = f"bot_add_{side}"
    players = ";".join(f'{add_command} "{player}"' for player in team.players)
    return f"{players};mp_teamlogo_{team_slot} {team.logo};mp_teamname_{team_slot} {team.name}"


def render_teams(teams: Iterable[Team]) -> list[str]:
    lines = [TEAM_BLOCK_START, ""]
    for index, team in enumerate(teams, start=1):
        lines.extend(
            [
                team.heading or f"{index}. {team.name}",
                "",
                command_line("ct", team, 1),
                "",
                command_line("t", team, 2),
                "",
            ]
        )
    return lines


def replace_team_block(commands_path: Path, teams: list[Team]) -> None:
    original = commands_path.read_text(encoding="utf-8").splitlines()
    try:
        start = original.index(TEAM_BLOCK_START)
        end = original.index(TEAM_BLOCK_END)
    except ValueError as exc:
        raise RuntimeError("Commands.txt must contain ADD TEAMS and COORDINATED BUY markers") from exc

    updated = original[:start] + render_teams(teams) + original[end:]
    commands_path.write_text("\n".join(updated) + "\n", encoding="utf-8")


def player_key(player_name: str) -> str:
    return re.sub(r"[^a-z0-9]", "", player_name.lower())


def align_players(existing_players: tuple[str, ...], scraped_players: tuple[str, ...]) -> tuple[str, ...]:
    remaining = list(scraped_players)
    aligned: list[str] = []
    for old_player in existing_players:
        old_key = player_key(old_player)
        exact_index = next(
            (index for index, player in enumerate(remaining) if player_key(player) == old_key),
            None,
        )
        if exact_index is not None:
            aligned.append(remaining.pop(exact_index))
        elif remaining:
            aligned.append(remaining.pop(0))

    aligned.extend(remaining)
    return tuple(aligned[:5])


def existing_teams(commands_path: Path) -> dict[str, ExistingTeam]:
    teams: dict[str, ExistingTeam] = {}
    heading = ""
    for line in commands_path.read_text(encoding="utf-8").splitlines():
        if re.match(r"^\d+\.\s*\S", line):
            heading = line
            continue

        if not line.startswith("bot_add_ct "):
            continue

        player_matches = re.findall(r'bot_add_ct "([^"]+)"', line)
        team_match = re.search(r"mp_teamlogo_1\s+(\S+);mp_teamname_1\s+(.+)$", line)
        if player_matches and team_match:
            name = team_match.group(2).strip()
            teams[team_key(name)] = ExistingTeam(
                heading=heading,
                name=name,
                players=tuple(player_matches[:5]),
                logo=team_match.group(1).strip(),
            )
    return teams


def main() -> int:
    args = parse_args()
    commands_path = Path(args.commands)
    teams = scrape_teams(
        args.url,
        args.proxy,
        args.max_teams,
        args.headless,
        existing_teams(commands_path),
    )
    if not teams:
        print("No teams were scraped from HLTV.", file=sys.stderr)
        return 1

    replace_team_block(commands_path, teams)
    print(f"Updated {commands_path} with {len(teams)} HLTV teams.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
