"""Two-copy sync test for Ranching Together on one PC.

usage: python -I qa/run_sync_test.py --host-ip <LAN IPv4> [--settings qa/local/settings.json] [--keep]

Starts a host copy and a client copy of the game with the CoopQA bot, connects them, then replays the two bugs
players reported: a slime launched while players are apart froze then snapped back, and spamming put/take at a
silo duplicated food. Prints RESULT: PASS or RESULT: FAIL (exit code 0 or 1).
Needs coop-anything tools/gamepool and instances provisioned from a copy of the game (see qa/README.md).
The real save folder and install are hashed before and after; any change is a FAIL.
"""
import argparse, json, os, sys, time, traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I leaves the script folder out of the import path
from sync_bench import CLIENT_ID, HOST_ID, PORT, Bench, actor, check, checks, distance, tree_hash, wait


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
    away = [distance(origin, p) for p in client_track]
    print(f"   {label}: distance from the launch point every 0.25 s: {[round(d, 1) for d in away]}", flush=True)
    # A live slime hops around after landing, but a snap back is a jump of meters toward the launch point at once.
    backward = max((a - b for a, b in zip(away, away[1:])), default=0)
    check(f"{label}: the slime moved on the client", max(away) > 2, f"farthest={max(away):.1f} m")
    check(f"{label}: no snap back to the launch point", backward < 3, f"largest step back={backward:.1f} m in 0.25 s")
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


def collector_scenario(bench):
    """The client drops five plorts by a plort collector and starts it: each plort must be stored once, on both."""
    for index in (0, 1):
        for collector in range(6):
            bench.cmd(index, f"watch_collector {collector}")
    time.sleep(3)
    totals = bench.state(0).get("collectors", {})
    collector = next((int(i) for i, count in sorted(totals.items()) if 0 < count <= 90), None)
    if not check("collector: found a collector with room", collector is not None, str(totals)):
        return
    for index in (0, 1):
        bench.cmd(index, f"teleport_collector {collector}")
    time.sleep(8)
    before = bench.state(0).get("collectors", {}).get(str(collector))
    bench.cmd(1, f"spawn_collector PinkPlort {collector} 5")
    time.sleep(4)
    bench.cmd(1, f"collect {collector}")
    time.sleep(15)
    host = bench.state(0).get("collectors", {}).get(str(collector))
    client = bench.state(1).get("collectors", {}).get(str(collector))
    gained = (host or 0) - (before or 0)
    check("collector: same content on both", host == client, f"{host} vs {client}")
    check("collector: the plorts were collected", gained >= 1, f"gained {gained}")
    check("collector: no plort stored twice", gained <= 5, f"gained {gained} for 5 plorts")


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
    collector_scenario(bench)

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
    except Exception:  # a crashed scenario must show up as a failure, not as a shorter run
        traceback.print_exc()
        check("all scenarios ran", False)
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


if __name__ == "__main__":
    main()
