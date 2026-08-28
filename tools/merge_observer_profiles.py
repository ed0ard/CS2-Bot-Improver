#!/usr/bin/env python3
"""Merge Liquipedia observer/caster names into CS2-Bot-Improver bot data.

- Appends marked profile sections to overrides/{Low,Medium,High}/botprofile.db.
- Adds the same names to addons/BotHider/bot_info.json "players" so the
  BotObserver plugin (which reads that file at runtime) sees them too.

Idempotent: existing names are skipped on re-runs. Template combos are cycled
from each db's own existing profiles, so each difficulty keeps its skill values.
"""

import gzip
import json
import re
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
DB_FILES = [
    REPO / "overrides" / "Low" / "botprofile.db",
    REPO / "overrides" / "Medium" / "botprofile.db",
    REPO / "overrides" / "High" / "botprofile.db",
]
BOT_INFO = REPO / "addons" / "BotHider" / "bot_info.json"

STEAMID64_BASE = 76561197960265728
LIQUIPEDIA_API = "https://liquipedia.net/counterstrike/api.php"
USER_AGENT = "CS2-Bot-Improver/1.0 (github.com/ed0ard/CS2-Bot-Improver)"
REQUEST_PACE_S = 4.0
CACHE_PATH = Path(r"D:\Temp\opencode\liquipedia_steam_cache.json")

STEAM64_RE = re.compile(
    r"steam(?:64)?[Ii][Dd]\s*=\s*(\d{17})"
    r"|steamcommunity\.com/profiles/(\d{17})"
    r"|\|\s*steam\s*=\s*(\d{17})"
)

MARKER = "// BotObserver: Liquipedia observer/caster profiles (auto-generated, do not edit below)"

OBSERVERS = [
    "ANKOR", "Ashix", "Bleq", "Cameron King", "Chezpuf", "ControL", "Dokai", "ElDoctorMuller", "Encg",
    "Esio", "EVAN", "Focuzz", "Frazer", "Frosty", "Goral", "Haloflyer", "HapCiu", "Ikosh", "ItsRandall",
    "Jak3y", "Janixs", "Jeyrazz", "JNZ", "Kejser", "Kioshi", "Klusia", "Kojtas", "Komodo", "Kub3",
    "KVIN S", "Lancemi", "Lefomit", "Lolbanelor", "Loxar", "Mata", "MC", "Migz1", "MikeR", "MILENK0",
    "Milo", "MIRVGE", "Misty", "PAn", "ParfeN", "Pekz", "PiciBear", "Pinqu", "Prius", "PsychoAlexeiz",
    "PythianLegume", "RaveN", "Rollen", "Rushly", "SapphiRe", "Sharley", "Shev", "Sliggy", "Swelder",
    "Szuwar", "UnknownFME", "Wan43r", "Zarx", "Zsokker",
]

CASTERS = [
    "117", "1obockZ'", "1uke", "2GD", "666", "7licious",
    "Advent", "Affinity", "Aiwa", "Alex", "AllInGaming", "AMD",
    "Anders Blume", "ANGeldusT", "Anishared", "AnThran", "Anyazita", "Apples",
    "Ash", "ASkalone", "Atro", "Aunni", "Babam", "BAC9CKOPOCTb",
    "Bananides", "BanGa", "Bczz", "BDog", "Beatriste", "Benccce",
    "BENJ", "BhT-", "BiDa", "Bleh", "BLU", "Boggs",
    "Boogie", "Boq", "Brainstorm", "BTT*", "Buckethead", "BurntSanctuary",
    "Byfield", "C0sta", "CaptainMo", "CaspeRRR", "Ceh9", "Celowh",
    "Chef Gree", "CHERRY5", "Chewwy", "Chobra", "CirmaN", "Clover",
    "Cmd64", "Cnd", "Cobaia", "CodyCasts", "Conky", "Coontag",
    "Counterfeit", "Craw", "CrystalMay", "Cudder", "CyberFocus", "Da bears",
    "Dabo0", "Damage", "DanChan", "DarfMike", "DAVEY", "Ddk",
    "Dean", "DEFINE", "Deman", "Dersor", "DeSraY", "Destru",
    "Dev1", "DG", "Dinko", "DK", "Dktruman", "Dolan",
    "Dona", "DONOD", "Drenotur", "Dugaid", "DunN", "DusT",
    "DutchBoy", "Dweg", "Ed101", "Efecdb", "Elfishguy", "Emenjay",
    "Emode", "Enkanis", "ExWarrior", "Fakel", "Falba", "FANAT ROCKA",
    "Feelin", "FF", "Fippe", "Flakes", "Flamboyant", "Fr4son",
    "Freesty1e", "Fujin", "Fv", "G0tt", "Gabesson", "Gaules",
    "GETTHEBAGMARK", "GieTe", "God Seven", "GooDW1N", "GORDOx", "GrimyRannarr",
    "Gromjkeee", "GromoZeka", "Grt", "GuizaO", "H1RO", "Hawka",
    "HAzE", "Hazza", "Headshinsky", "Heliumbrella", "HenryG", "Hermes",
    "Herold", "HiyaNoot", "HomeR", "Horstor", "Hoven", "Hugo Byron",
    "Hyferia", "Hyper", "Ic3-Man", "Index", "Ink-mate", "IpSa",
    "Izak", "Jacky", "Jacob", "JAEGARN", "James Bardolph", "Jawed",
    "Jaxi", "JimNeso", "Jitko", "JKaplan", "Joe Miller", "Joshua",
    "JRT", "JustHarry", "K1smagyar", "KaRath", "KEi", "Keith LaFortune",
    "KevinMPV", "KirosZ", "Knochen", "KODIAK", "Koty", "KREATIV",
    "Kremer3", "KRL", "KubiK", "Kuroxy", "KvaN", "Kyan1te",
    "Lalok", "Launders", "Laz", "Leniniw", "Leq", "Leskisson",
    "LighteRTZ", "Ljuba", "Loran", "Lore", "Lrz", "LucyLuce",
    "Luddie", "Lyssan", "Mac", "Machine", "MachineWJQ", "Mad1",
    "MagicHelmet", "MagiskaAbbas", "Malik", "ManicMunday", "Marcatto", "Mare",
    "Masterplay", "Mauisnake", "Megaman", "Mett", "MintGod", "Mishek",
    "Missivy", "MitchMan", "Mito", "Mjpinkman", "Moreira", "Morgen",
    "Moses", "MPKBK", "Mucha", "MukhaS", "N0thing", "NABOWOW",
    "NaoriMizuki", "Ne0kai", "Nessyteras", "Nexus", "Nicolino", "Okroshka",
    "Olsior", "Olvari", "OnlyJoshinTV", "Oversiard", "Paladin", "Pansy",
    "PCH3LK1N", "Peach", "Peekay", "Petrovich", "Phy", "PIKA",
    "Pilski", "Pineapple Philips", "Pitu Herranz", "Pm", "Pop", "PRAWUs",
    "PsYcOrN", "QeP", "QUQU", "Qz", "RauleS", "Rdl",
    "Redeye", "Redulj", "Rema", "ReTr00", "RickyDC", "Rizc",
    "RobuJohnson", "Sadokist", "Sakula", "SanCor", "SandMan", "Savage",
    "Sc0rch", "Scrawny", "Searph", "Semmler", "Sergiz", "Serhman",
    "SeveralSheep", "Shadye", "Sheyl", "Shiny", "Shoker", "Shoushou",
    "Silv8", "Sir Danny", "Skriv", "SL4M", "Slam", "SlaxerCast",
    "Sleepsomewhile", "Slop3", "SLowi", "SneakyBeaky", "Solodana", "Spat1ch",
    "SpeeDy", "SPUNJ", "Squid", "STORM", "Straty", "Strike",
    "StrongLegs", "Stunna", "Svojke", "Sxstem", "Syon", "Szeregowy",
    "T0ks", "Tb", "TeaTime", "Tedd", "Tenshi", "TheCraggy",
    "TheEternalJay", "Theluchs4363", "TheRA1DER", "ThoreQ", "Thunder", "TIMMO",
    "Toivo", "Toldersma", "Toll", "Tom Pickering", "Tombizz", "Tomy",
    "Topical", "TosspoT", "Trav", "Tutsi", "TwoTapTony", "Uber",
    "Upmind", "Urf", "UzDevice", "V1nson", "V3n0m", "Valencho",
    "Vansilli", "Vearless", "Veracity", "Viick", "Vince", "Volt",
    "Voltie", "Voo", "Vuggo", "Vuzzey", "Warclown", "Werty",
    "WH1TE", "Wildeity", "Wolfy6678", "X9nium", "Xf", "XiaosaGe",
    "XiTTeN", "Xixi", "Xner", "XppE", "Xrm", "YangZai",
    "YJ", "YNk", "YnoT", "YouM3", "Yukio", "YXo",
    "Zais", "Zbr4", "Zerpherr", "Zescht", "Zloba", "Zlynx",
    "ZolaSMH", "ZoneR", "ZorlaK", "ZZW",
]

POOL = OBSERVERS + CASTERS

# Profile block header, e.g.  ProSlow+SniperPro+SniperPersonality "Zhang Weiwei"
HEADER_RE = re.compile(r'^(\S+)\s+"(.+)"\s*$')


def parse_db(text: str):
    names = set()
    combos = []
    for line in text.splitlines():
        m = HEADER_RE.match(line)
        if not m:
            continue
        names.add(m.group(2))
        if m.group(1) not in combos:
            combos.append(m.group(1))
    return names, combos


def voice_pitch(name: str) -> int:
    return 85 + (sum(ord(c) for c in name) * 7) % 31


def merge(path: Path) -> int:
    text = path.read_text(encoding="utf-8", errors="replace")

    if MARKER in text:
        head, _, _ = text.partition(MARKER)
        head = head.rstrip("\n")
        text = head

    names, combos = parse_db(text)
    if not combos:
        print(f"[error] {path.name}: no profile headers found", file=sys.stderr)
        return 1

    pending = [n for n in POOL if n not in names and n.lower() not in {x.lower() for x in names}]
    if not pending:
        print(f"[skip] {path.name}: nothing new to add")
        return 0

    section = ["\n" + MARKER]
    for i, name in enumerate(pending):
        combo = combos[i % len(combos)]
        section.append(f'{combo} "{name}"')
        section.append(f"\tVoicePitch = {voice_pitch(name)}")
        section.append("End")
    section.append("")

    path.write_text(text + "\n".join(section), encoding="utf-8", newline="")
    print(f"[ok] {path.name}: added {len(pending)} profiles ({len(combos)} template combos reused)")
    return len(pending)


class LiquipediaRateLimit(Exception):
    """Raised immediately when Liquipedia returns HTTP 429; carries the URL."""


def load_cache() -> dict:
    if CACHE_PATH.exists():
        try:
            return json.loads(CACHE_PATH.read_text(encoding="utf-8"))
        except Exception:
            pass
    return {}


def save_cache(cache: dict) -> None:
    CACHE_PATH.parent.mkdir(parents=True, exist_ok=True)
    CACHE_PATH.write_text(json.dumps(cache, ensure_ascii=False, indent=2), encoding="utf-8")


def fetch_steam64(name: str, cache: dict, use_network: bool = True) -> int:
    """Return SteamID64 for a Liquipedia page name, or 0 when unavailable."""
    key = name.lower()
    if key in cache:
        return cache[key]
    if not use_network:
        return 0

    url = (
        f"{LIQUIPEDIA_API}?action=parse&page={urllib.parse.quote(name)}"
        "&prop=wikitext&format=json&formatversion=2&redirect=1"
    )
    sid = 0
    cached = False
    for attempt in (1, 2, 3):
        try:
            req = urllib.request.Request(url, headers={
                "User-Agent": USER_AGENT,
                "Accept-Encoding": "gzip",
            })
            with urllib.request.urlopen(req, timeout=15) as resp:
                raw = resp.read()
                if resp.headers.get("Content-Encoding") == "gzip" or raw[:2] == b"\x1f\x8b":
                    raw = gzip.decompress(raw)
                data = json.loads(raw.decode("utf-8", errors="replace"))
            wikitext = data.get("parse", {}).get("wikitext", "")
            m = STEAM64_RE.search(wikitext or "")
            if m:
                sid = int(next(g for g in m.groups() if g))
            cached = True
            break
        except urllib.error.HTTPError as e:
            if e.code == 429:
                raise LiquipediaRateLimit(url)
            if attempt < 3:
                time.sleep(2)
            else:
                print(f"    [warn] {name}: HTTP {e.code}")
        except Exception as e:
            if attempt == 3:
                print(f"    [warn] {name}: {type(e).__name__}: {e}")
            else:
                time.sleep(2)
    if cached:
        cache[key] = sid
    time.sleep(REQUEST_PACE_S)
    return sid


def merge_bot_info(use_network: bool) -> int:
    data = json.loads(BOT_INFO.read_text(encoding="utf-8"))
    players = data.setdefault("players", {})
    disabled = data.get("disabled_players", {})

    existing = {
        str(v.get("player_name", "")).lower()
        for v in players.values()
        if isinstance(v, dict)
    }
    existing |= {str(k).lower() for k in disabled.keys()}

    used_keys = {str(k) for k in players.keys()}
    cache = load_cache()
    dirty = False

    try:
        next_id = max(int(k) for k in players.keys() if str(k).isdigit()) + 1
    except ValueError:
        next_id = 100000

    added = 0
    for name in POOL:
        if name.lower() in existing:
            continue

        try:
            sid64 = fetch_steam64(name, cache, use_network)
        except LiquipediaRateLimit as e:
            save_cache(cache)
            print(f"[429] rate limited, aborting. Blocked URL: {e}", file=sys.stderr)
            print("[429] progress was saved incrementally; re-run later to resume.", file=sys.stderr)
            return -1
        if sid64:
            account_id = sid64 - STEAMID64_BASE
            key = str(account_id)
            if not (0 < account_id < 2**32) or key in used_keys:
                sid64 = 0

        if not sid64:
            while str(next_id) in used_keys:
                next_id += 1
            key = str(next_id)
            next_id += 1

        players[key] = {"player_name": name, "scoreboard_flair": 0}
        used_keys.add(key)
        existing.add(name.lower())
        added += 1
        dirty = True
        src = f"steam64={sid64}" if sid64 else "synthetic"
        print(f"    + {name} -> {key} ({src})")

        BOT_INFO.write_text(json.dumps(data, ensure_ascii=False, indent=4) + "\n", encoding="utf-8")

    if dirty:
        save_cache(cache)
    print(f"[ok] bot_info.json: added {added} entries")
    return added


def main() -> int:
    use_network = "--no-steam" not in sys.argv

    total = 0
    for db in DB_FILES:
        if not db.exists():
            print(f"[error] missing: {db}", file=sys.stderr)
            return 1
        total += merge(db)

    if not BOT_INFO.exists():
        print(f"[error] missing: {BOT_INFO}", file=sys.stderr)
        return 1

    added = merge_bot_info(use_network)
    if added < 0:
        return 2

    print(f"done: {total} profiles added")
    return 0


if __name__ == "__main__":
    sys.exit(main())
