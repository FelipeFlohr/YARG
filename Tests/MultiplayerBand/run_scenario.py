"""Host-side runner: python3 run_scenario.py <scenario> [--dry-run] [--stagger s]"""
import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys

import check_band_logs
from scenarios import SCENARIOS

HERE = os.path.dirname(os.path.abspath(__file__))
HOME = os.path.expanduser("~")
RUNS = os.path.join(HOME, "yarg-mp-banda-runs")
BUILD = os.path.join(HOME, "yarg-build-mp-banda")
SONGS = os.path.join(HOME, "repos", "musicas yarg")
BASE_SETTINGS = {
    "DisablePerSongBackgrounds": True,
    "DisableGlobalBackgrounds": True,
    "DisableDefaultBackground": True,
    "NoFail": 1,
    "AllowRemoteContent": False,
}


def docker_command(name, run_dir, stagger):
    gids = [os.stat(p).st_gid for p in ("/dev/dri/renderD128", "/dev/dri/card0", "/dev/snd/seq")]
    cmd = ["timeout", "1500", "docker", "run", "--rm", "--name", "yarg-test-" + name, "--network", "none",
           "--device", "/dev/dri", "--device", "/dev/snd/seq"]
    for g in gids:
        cmd += ["--group-add", str(g)]
    cmd += ["--user", "%d:%d" % (os.getuid(), os.getgid()), "-e", "HOME=/work",
            "-v", run_dir + ":/work", "-v", BUILD + ":/game:ro", "-v", SONGS + ":/songs:ro",
            "-v", HERE + ":/tests:ro", "yarg-mp-banda",
            "python3", "/tests/launch_in_container.py", name, "--stagger", str(stagger)]
    return cmd


def prepare(name, run_dir):
    if os.path.isdir(run_dir):
        shutil.rmtree(run_dir)
    for inst in SCENARIOS[name]["instances"]:
        data = os.path.join(run_dir, inst["name"])
        os.makedirs(data)
        settings = dict(BASE_SETTINGS)
        if inst["role"] == "host":
            settings["SongFolders"] = ["/songs"]
        with open(os.path.join(data, "settings.json"), "w") as f:
            json.dump(settings, f, indent=2)


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("scenario", choices=sorted(SCENARIOS))
    parser.add_argument("--stagger", type=float, default=5.0)
    parser.add_argument("--dry-run", action="store_true", help="only print the docker command")
    args = parser.parse_args(argv)

    run_dir = os.path.join(RUNS, args.scenario)
    cmd = docker_command(args.scenario, run_dir, args.stagger)
    if args.dry_run:
        print(" ".join(shlex.quote(c) for c in cmd))
        return 0

    prepare(args.scenario, run_dir)
    code = subprocess.call(cmd)
    print("container exit code: %d" % code)

    logs = {}
    for inst in SCENARIOS[args.scenario]["instances"]:
        src = os.path.join(run_dir, inst["name"], "player.log")
        dst = os.path.join(run_dir, inst["name"] + ".log")
        if os.path.exists(src):
            shutil.copyfile(src, dst)
    return max(1 if code != 0 else 0, check_band_logs.main([args.scenario, run_dir]))


if __name__ == "__main__":
    sys.exit(main())
