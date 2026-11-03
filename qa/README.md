# Two-copy sync tests

Automated tests that run two real copies of Slime Rancher 2 on one PC, a host and a client, connect them with
Ranching Together and check the sync. The method and the tools come from
[coop-anything](https://github.com/cranebeaker/coop-anything) (MIT): `tools/gamepool` for the copies and its
Slime Rancher 2 bot, extended here.

## What the test checks

`run_sync_test.py` replays the two bugs players reported, plus a smoke test:

| Scenario | Checks |
|---|---|
| Connection | both copies load their own save, the host starts, the client connects, each sees the other |
| Launch while apart | the client launches a slime: it keeps flying, never snaps back to the launch point, and the launcher simulates it during the flight |
| Launch side by side | the same, plus the host's copy ends where the client's is and exactly one copy simulates the slime |
| Silo input | the client drops 5 carrots into a silo input: both copies end with the same content and no item is counted twice |
| Silo spam | the client puts in and takes out one item per frame, 60 times: both copies end with the same content and nothing is created |

It also hashes the real save folder and the real `Mods` folder before and after, and fails if anything changed.

## Parts

- `Bot/`: `CoopQA.Bot`, a MelonLoader mod that does nothing unless `UserData/CoopQA.Bot.ini` exists. It loads a
  save at boot, gives each copy its own player id, runs numbered commands from `UserData/CoopQA/cmd.txt` and writes
  `UserData/CoopQA/state.json` four times a second.
- `SaveIsolation/`: `CoopQA.SaveIsolation`, a MelonLoader plugin from coop-anything (unchanged). It points the
  game's save folder to the copy's own folder, so the real saves and the Steam Cloud folder are never touched.
- `run_sync_test.py`: deploys the mod builds into the copies, starts them, drives them and prints
  `RESULT: PASS` or `RESULT: FAIL`.

## Setup (once)

1. Copy the game folder (with MelonLoader and Starlight installed) somewhere else, for example `C:\SR2QA\game`.
2. Get coop-anything and write a gamepool config from
   `games/slime-rancher-2/qa/sr2.example.json` pointing `game_dir` to that copy. Then:

   ```
   python -I <coop-anything>/tools/gamepool/gamepool.py --config <config> provision --n 2
   ```

   `provision` leaves the per-copy folders empty. Copy into each `<instances>/<n>` folder, from the game copy:
   `version.dll`, `MelonLoader/`, `UserLibs/`, `UserData/` and `Mods/Starlight.dll`. Without them MelonLoader
   does not start, so the save isolation does not run either; the test refuses to start a copy in that state.

3. Build the mod and the test parts:

   ```
   dotnet build SR2MP -c Release
   dotnet build qa/Bot -c Release
   dotnet build qa/SaveIsolation -c Release -p:ML="<game dir>\MelonLoader"
   ```

4. Create `qa/local/settings.json` (ignored by git) with your own paths:

   ```json
   {
     "gamepool_script": "<coop-anything>/tools/gamepool/gamepool.py",
     "gamepool_config": "<gamepool config>",
     "gamepool_dir": "<folder for gamepool state>",
     "instances_root": "<instances folder from the config>",
     "steamid": "<your steamid64>",
     "saves_dir": "<folder with copies of the two saves>",
     "host_save": "<host save file name, a ranch with at least one silo>",
     "client_save": "<client save file name>",
     "sr2mp_dll": "SR2MP/bin/Release/net6.0/SR2MP.dll",
     "bot_dll": "qa/Bot/bin/Release/net6.0/CoopQA.Bot.dll",
     "save_isolation_dll": "qa/SaveIsolation/bin/Release/net6.0/CoopQA.SaveIsolation.dll",
     "real_paths": ["%USERPROFILE%/AppData/LocalLow/MonomiPark/SlimeRancher2", "<real game dir>/Mods"]
   }
   ```

## Run

```
python -I qa/run_sync_test.py --host-ip <this PC's LAN IPv4>
```

The client refuses `127.0.0.1`, so use the LAN address. Add `--keep` to leave the copies running.
Each copy needs about 3.5 GB of RAM.
