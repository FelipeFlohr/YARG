"""Host-side runner: python3 run_scenario.py <scenario> [--native] [--dry-run] [--stagger s]

Without --native the instances run in Docker (Linux build, Xvfb, software rendering). With --native they run as
plain processes on this machine with its own GPU and sound card (Windows or Linux build).
"""
import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys

import check_band_logs
import launch_in_container
from scenarios import SCENARIOS

HERE = os.path.dirname(os.path.abspath(__file__))
HOME = os.path.expanduser("~")
RUNS = os.path.join(HOME, "yarg-mp-banda-runs")
BUILD = os.path.join(HOME, "yarg-build-mp-banda")
SONGS = os.environ.get("YARG_MP_SONGS", os.path.join(HOME, "repos", "musicas yarg"))
NATIVE_GAME = os.path.join(BUILD, "YARG.exe" if os.name == "nt" else "YARG.x86_64")
NATIVE_WINDOW_FLAGS = ["-screen-fullscreen", "0", "-screen-width", "640", "-screen-height", "360"]
BASE_SETTINGS = {
    "DisablePerSongBackgrounds": True,
    "DisableGlobalBackgrounds": True,
    "DisableDefaultBackground": True,
    "NoFail": 1,
    "AllowRemoteContent": False,
}


def docker_command(name, run_dir, stagger):
    gids = [os.stat(p).st_gid for p in ("/dev/dri/renderD128", "/dev/dri/card0", "/dev/snd/seq")]
    cmd = ["timeout", "-k", "30", "1500", "docker", "run", "--init", "--rm", "--name", "yarg-test-" + name, "--network", "none",
           "--device", "/dev/dri", "--device", "/dev/snd/seq"]
    for g in gids:
        cmd += ["--group-add", str(g)]
    cmd += ["--user", "%d:%d" % (os.getuid(), os.getgid()), "-e", "HOME=/work",
            "-v", run_dir + ":/work", "-v", BUILD + ":/game:ro", "-v", SONGS + ":/songs:ro",
            "-v", HERE + ":/tests:ro", "yarg-mp-banda",
            "python3", "/tests/launch_in_container.py", name, "--stagger", str(stagger)]
    return cmd


def prepare(name, run_dir, songs="/songs", extra_settings=None):
    if os.path.isdir(run_dir):
        shutil.rmtree(run_dir)
    scenario = SCENARIOS[name]
    for inst in scenario["instances"]:
        data = os.path.join(run_dir, inst["name"])
        os.makedirs(data)
        settings = dict(BASE_SETTINGS)
        settings.update(scenario.get("settings", {}))
        settings.update(extra_settings or {})
        if inst["role"] == "host":
            settings["SongFolders"] = [songs]
        with open(os.path.join(data, "settings.json"), "w") as f:
            json.dump(settings, f, indent=2)


def run_native(name, run_dir, args):
    if not os.path.isfile(args.game):
        print("no game build at %s (see README.md, Build)" % args.game)
        return 2
    if not os.path.isdir(args.songs):
        print("no song folder at %s (--songs or YARG_MP_SONGS)" % args.songs)
        return 2

    scenario = SCENARIOS[name]
    # Sound off unless asked for: several instances at once are just noise
    prepare(name, run_dir, songs=os.path.abspath(args.songs),
            extra_settings=None if args.audible else {"MasterMusicVolume": 0})
    return launch_in_container.run_instances(
        scenario,
        lambda inst: launch_in_container.command(scenario, inst, game=args.game, work=run_dir,
                                                 window_flags=NATIVE_WINDOW_FLAGS),
        args.stagger, host_wait=args.host_wait)


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("scenario", choices=sorted(SCENARIOS))
    parser.add_argument("--stagger", type=float, default=5.0)
    parser.add_argument("--dry-run", action="store_true", help="only print the docker command")
    parser.add_argument("--native", action="store_true", help="run the build directly on this machine, no Docker")
    parser.add_argument("--game", default=NATIVE_GAME, help="native: the player executable")
    parser.add_argument("--songs", default=SONGS, help="native: the host's song folder")
    parser.add_argument("--host-wait", type=float, default=launch_in_container.HOST_WAIT,
                        help="native: seconds between the host and the first client")
    parser.add_argument("--audible", action="store_true", help="native: keep the game's sound on")
    args = parser.parse_args(argv)

    run_dir = os.path.join(RUNS, args.scenario)
    if args.native:
        code = run_native(args.scenario, run_dir, args)
        print("instances exit code: %d" % code)
    else:
        cmd = docker_command(args.scenario, run_dir, args.stagger)
        if args.dry_run:
            print(" ".join(shlex.quote(c) for c in cmd))
            return 0

        subprocess.call(["docker", "rm", "-f", "yarg-test-" + args.scenario],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        prepare(args.scenario, run_dir)
        code = subprocess.call(cmd)
        print("container exit code: %d" % code)

    for inst in SCENARIOS[args.scenario]["instances"]:
        src = os.path.join(run_dir, inst["name"], "player.log")
        dst = os.path.join(run_dir, inst["name"] + ".log")
        if os.path.exists(src):
            shutil.copyfile(src, dst)
    return max(1 if code != 0 else 0, check_band_logs.main([args.scenario, run_dir]))


if __name__ == "__main__":
    sys.exit(main())
