"""Two-copy sync test for Ranching Together on one PC.

usage: python -I qa/run_sync_test.py --host-ip <LAN IPv4> [--settings qa/local/settings.json] [--keep]

Starts a host copy and a client copy of the game with the CoopQA bot, connects them, then replays the two bugs
players reported: a slime launched while players are apart froze then snapped back, and spamming put/take at a
silo duplicated food. Prints RESULT: PASS or RESULT: FAIL (exit code 0 or 1).
Needs coop-anything tools/gamepool and instances provisioned from a copy of the game (see qa/README.md).
The real save folder and install are hashed before and after; any change is a FAIL.
"""
import argparse, hashlib, json, math, os, shutil, subprocess, sys, time
from pathlib import Path

HOST_ID, CLIENT_ID = "PLAYER_QAHOST001", "PLAYER_QACLNT002"
PORT = 1919
checks = []


def check(name, ok, detail=""):
    checks.append((name, bool(ok), detail))
    print(f"[{'PASS' if ok else 'FAIL'}] {name} {detail}", flush=True)
    return ok


def tree_hash(root):
    return {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(root.rglob("*")) if p.is_file()}


class Bench:
    def __init__(self, settings):
        self.s = settings
        self.instances = Path(settings["instances_root"])
        self.sequence = {0: 0, 1: 0}

    def gamepool(self, *args):
        env = dict(os.environ, GAMEPOOL_DIR=self.s["gamepool_dir"])
        result = subprocess.run([sys.executable, "-I", self.s["gamepool_script"], "--config", self.s["gamepool_config"], *args],
                                capture_output=True, text=True, timeout=900, env=env)
        print(result.stdout.strip(), result.stderr.strip(), flush=True)
        return result.returncode

    def deploy(self, index, role, save, player_id):
        root = self.instances / str(index)
        for old in (root / "Mods").glob("SR2MP*.dll"):
            old.unlink()
        shutil.copy2(self.s["sr2mp_dll"], root / "Mods" / "SR2MP.dll")
        shutil.copy2(self.s["bot_dll"], root / "Mods" / "CoopQA.Bot.dll")
        shutil.copy2(self.s["save_isolation_dll"], root / "Plugins" / "CoopQA.SaveIsolation.dll")

        save_dir = root / "LocalLow/MonomiPark/SlimeRancher2/Steam" / self.s["steamid"]
        if save_dir.exists():
            shutil.rmtree(save_dir)
        save_dir.mkdir(parents=True)
        shutil.copy2(Path(self.s["saves_dir"]) / save, save_dir / save)

        (root / "UserData/CoopQA").mkdir(parents=True, exist_ok=True)
        for name in ("cmd.txt", "state.json"):
            (root / "UserData/CoopQA" / name).unlink(missing_ok=True)
        (root / "UserData/CoopQA.Bot.ini").write_text(
            f"role={role}\nplayer_id={player_id}\nsave={(save_dir / save).as_posix()}\n", encoding="utf-8")

        # Without MelonLoader the isolation plugin never runs and the copy would write to the real save folder.
        required = ["version.dll", "MelonLoader/net6/MelonLoader.dll", "Plugins/CoopQA.SaveIsolation.dll",
                    "UserData/CoopQA.SaveRoot.txt", "Mods/Starlight.dll"]
        missing = [name for name in required if not (root / name).exists()]
        if missing:
            sys.exit(f"copy {index} is not ready to run safely, missing: {', '.join(missing)} (see qa/README.md)")

    def state(self, index):
        try:
            return json.loads((self.instances / str(index) / "UserData/CoopQA/state.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def cmd(self, index, text):
        self.sequence[index] += 1
        with open(self.instances / str(index) / "UserData/CoopQA/cmd.txt", "a", encoding="utf-8") as handle:
            handle.write(f"{self.sequence[index]} {text}\n")


def wait(fn, timeout):
    start = time.time()
    while time.time() - start < timeout:
        value = fn()
        if value:
            return value
        time.sleep(0.5)
    return None


def distance(a, b):
    return math.dist(a, b) if a and b else float("inf")


def actor(bench, index, tag):
    return bench.state(index).get("actors", {}).get(tag, {})


def launch_scenario(bench, tag, label):
    """The client launches a slime. It must keep flying on the client and the host must end where the client does."""
    bench.cmd(1, f"spawn_ahead Pink {tag} 3 12 6")
    actor_id = wait(lambda: bench.state(1).get("spawned", {}).get(tag), 20)
    if not check(f"{label}: client spawned the slime", actor_id):
        return
    bench.cmd(0, f"watch {actor_id} {tag}")

    samples, start = [], time.time()
    while time.time() - start < 5:
        samples.append((time.time() - start, actor(bench, 1, tag), actor(bench, 0, tag)))
        time.sleep(0.25)

    client_track = [c["pos"] for _, c, _ in samples if c.get("pos")]
    owned_early = [c.get("owned") for t, c, _ in samples if t < 1.5 and c.get("pos")]
    if not check(f"{label}: client reports the slime", len(client_track) > 4, f"samples={len(client_track)}"):
        return

    origin = client_track[0]
    farthest = max(distance(origin, p) for p in client_track)
    final = distance(origin, client_track[-1])
    check(f"{label}: the slime moved on the client", farthest > 2, f"farthest={farthest:.1f} m")
    check(f"{label}: no snap back to the launch point", final >= 0.6 * farthest, f"final={final:.1f} farthest={farthest:.1f}")
    check(f"{label}: the launcher simulates it during the flight", owned_early and all(owned_early), str(owned_early))

    host_final = samples[-1][2]
    if host_final.get("pos"):
        gap = distance(host_final["pos"], client_track[-1])
        check(f"{label}: host copy ends where the client's is", gap < 3, f"gap={gap:.1f} m")
        # A handoff can happen during the flight: read several times, never two simulators, mostly exactly one.
        readings = []
        for _ in range(8):
            readings.append([actor(bench, 1, tag).get("owned"), actor(bench, 0, tag).get("owned")])
            time.sleep(0.25)
        single = sum(r.count(True) == 1 for r in readings)
        check(f"{label}: never simulated by both copies", all(r.count(True) < 2 for r in readings), str(readings))
        check(f"{label}: exactly one copy simulates it", single >= 6, f"{single}/8 readings")
    else:
        print(f"   {label}: slime not loaded on the host (players far apart), host side skipped", flush=True)


def silo_report(bench, index, silo):
    return bench.state(index).get("silos", {}).get(str(silo))


def silo_content(bench, index, silo):
    report = silo_report(bench, index, silo)
    return [entry[:2] for entry in report["slots"]] if report else None


def total(content):
    return sum(count for _, count in content or [])


def usable_silo(bench, candidates):
    """A silo input whose slot holds items and has room for five more (type "auto"), else an empty one (carrots)."""
    empty = None
    for silo in candidates:
        report = silo_report(bench, 0, silo)
        if not report or report["slot"] >= len(report["slots"]):
            continue
        name, count, maximum = report["slots"][report["slot"]]
        if name and count >= 2 and maximum - count >= 6:
            return silo, "auto"
        if not name and empty is None:
            empty = (silo, "Carrot")
    return empty or (None, None)


def silo_scenario(bench):
    candidates = range(10)
    for index in (0, 1):
        bench.cmd(index, "teleport_silo 0")
        for silo in candidates:
            bench.cmd(index, f"watch_silo {silo}")
    time.sleep(6)
    silo, item = usable_silo(bench, candidates)
    if not check("silo: found a silo input to test", silo is not None, f"silo {silo} with {item}"):
        return
    for index in (0, 1):
        bench.cmd(index, f"teleport_silo {silo}")
    time.sleep(6)
    before = silo_content(bench, 0, silo)
    check("silo: same content before the test", before == silo_content(bench, 1, silo), f"{before} vs {silo_content(bench, 1, silo)}")

    for i in range(5):
        bench.cmd(1, f"spawn_catcher {item} C{i} {silo}")
        time.sleep(0.3)
    time.sleep(6)
    host, client = silo_content(bench, 0, silo), silo_content(bench, 1, silo)
    gained = total(host) - total(before)
    check("silo: items dropped in the input end up the same on both", host == client, f"{host} vs {client}")
    check("silo: the silo caught the items", gained >= 1, f"gained {gained}")
    check("silo: no item counted twice", gained <= 5, f"gained {gained} for 5 items")

    before_spam = total(silo_content(bench, 0, silo))
    bench.cmd(1, f"silo_spam {silo} {item} 60")
    wait(lambda: bench.state(1).get("pending_actions") == 0 and bench.state(1).get("last_cmd") == bench.sequence[1], 60)
    time.sleep(4)
    host, client = silo_content(bench, 0, silo), silo_content(bench, 1, silo)
    check("silo: take/put spam ends with the same content", host == client, f"{host} vs {client}")
    check("silo: take/put spam created or lost nothing", total(host) == before_spam, f"{before_spam} -> {total(host)}")


def run(bench, host_ip):
    if not check("both copies started", bench.gamepool("up", "--n", "2") == 0):
        return
    for index in (0, 1):
        check(f"copy {index} loaded its save", wait(lambda: bench.state(index).get("in_game"), 400))

    bench.cmd(0, f"host {PORT}")
    check("server running", wait(lambda: bench.state(0).get("server_running"), 30))
    bench.cmd(1, f"connect {host_ip} {PORT}")
    if not check("client connected", wait(lambda: bench.state(1).get("client_connected"), 90)):
        return
    check("host sees the client", wait(lambda: any(r["id"] == CLIENT_ID for r in bench.state(0).get("remote_players", [])), 60))
    time.sleep(15)

    apart = distance(bench.state(0).get("pos"), bench.state(1).get("pos"))
    print(f"players are {apart:.0f} m apart", flush=True)
    launch_scenario(bench, "far", "launch while apart")

    bench.cmd(1, f"teleport_near {HOST_ID} 4 0")
    time.sleep(8)
    launch_scenario(bench, "near", "launch side by side")

    silo_scenario(bench)

    for index in (0, 1):
        print(f"copy {index} events:", *bench.state(index).get("events", [])[-15:], sep="\n   ")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host-ip", required=True, help="this PC's LAN IPv4 (the client refuses 127.0.0.1)")
    parser.add_argument("--settings", default=str(Path(__file__).parent / "local" / "settings.json"))
    parser.add_argument("--keep", action="store_true", help="leave the copies running")
    parser.add_argument("--sr2mp-dll", help="test another build of the mod, for example the upstream release")
    args = parser.parse_args()

    settings = json.loads(Path(args.settings).read_text(encoding="utf-8"))
    if args.sr2mp_dll:
        settings["sr2mp_dll"] = args.sr2mp_dll
    bench = Bench(settings)
    real = [Path(os.path.expandvars(p)) for p in settings["real_paths"]]
    before = {str(p): tree_hash(p) for p in real}

    bench.deploy(0, "host", settings["host_save"], HOST_ID)
    bench.deploy(1, "client", settings["client_save"], CLIENT_ID)
    try:
        run(bench, args.host_ip)
    finally:
        if not args.keep:
            bench.gamepool("down")
            time.sleep(3)
        for path in real:
            after = tree_hash(path)
            changed = [k for k in set(before[str(path)]) | set(after) if before[str(path)].get(k) != after.get(k)]
            check(f"real folder unchanged: {path.name}", not changed, ", ".join(changed[:5]))
        passed = sum(ok for _, ok, _ in checks)
        print(f"\nRESULT: {'PASS' if passed == len(checks) else 'FAIL'}  ({passed}/{len(checks)} checks)")
        sys.exit(0 if passed == len(checks) else 1)


main()
