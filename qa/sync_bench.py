"""Shared tools of the two-copy sync test: drives the copies through gamepool and the CoopQA bot files."""
import hashlib, json, math, os, shutil, subprocess, sys, time
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
        line = f"{self.sequence[index]} {text}\n"
        for _ in range(20):  # the bot reads the file four times a second, which can lock it for a moment
            try:
                with open(self.instances / str(index) / "UserData/CoopQA/cmd.txt", "a", encoding="utf-8") as handle:
                    handle.write(line)
                return
            except PermissionError:
                time.sleep(0.1)
        raise PermissionError(f"cmd.txt of copy {index} stayed locked")


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
