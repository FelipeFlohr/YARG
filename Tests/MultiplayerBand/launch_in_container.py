"""Runs inside the container: Xvfb, then the host and the clients of one scenario."""
import argparse
import os
import subprocess
import sys
import time

from scenarios import SCENARIOS

GAME = "/game/YARG.x86_64"
WINDOW_FLAGS = ["-force-glcore", "-screen-fullscreen", "0", "-screen-width", "640", "-screen-height", "360"]
HOST_WAIT = 20
TOTAL_TIMEOUT = 1400


def command(scenario, inst):
    cmd = [GAME] + WINDOW_FLAGS + [
        "-persistent-data-path", "/work/" + inst["name"],
        "-logFile", "/work/%s/player.log" % inst["name"],
    ]
    if inst["role"] == "host":
        cmd += ["-mp-autotest", "host", "-mp-song", scenario["song"], "-mp-rounds", "1",
                "-mp-players", str(len(scenario["instances"]))]
    else:
        cmd += ["-mp-autotest", "join", "127.0.0.1"]
    return cmd + inst["args"]


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("scenario", choices=sorted(SCENARIOS))
    parser.add_argument("--stagger", type=float, default=5.0, help="seconds between client starts")
    args = parser.parse_args(argv)
    scenario = SCENARIOS[args.scenario]

    xvfb = subprocess.Popen(["Xvfb", ":99", "-screen", "0", "1280x720x24"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    env = dict(os.environ, DISPLAY=":99")
    time.sleep(2)
    procs = []
    try:
        for i, inst in enumerate(scenario["instances"]):
            if i == 1:
                time.sleep(HOST_WAIT)
            elif i > 1:
                time.sleep(args.stagger)
            print("starting " + inst["name"], flush=True)
            procs.append((inst["name"], subprocess.Popen(command(scenario, inst), env=env)))

        deadline = time.time() + TOTAL_TIMEOUT
        ok = True
        while time.time() < deadline and any(p.poll() is None for _, p in procs):
            failed = [n for n, p in procs if p.poll() not in (None, 0)]
            if failed:
                print("%s failed, killing the others" % ", ".join(failed), flush=True)
                ok = False
                break
            time.sleep(1)
        for name, p in procs:
            if p.poll() is None:
                print("killing " + name, flush=True)
                p.kill()
                p.wait()
                ok = False
            elif p.returncode != 0:
                print("%s exited with %d" % (name, p.returncode), flush=True)
                ok = False
        return 0 if ok else 1
    finally:
        for _, p in procs:
            if p.poll() is None:
                p.kill()
        xvfb.kill()


if __name__ == "__main__":
    sys.exit(main())
