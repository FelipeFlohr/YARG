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


def command(scenario, inst, game=GAME, work="/work", window_flags=WINDOW_FLAGS):
    """The command line of one instance; the native runner passes its own game, folder and window flags."""
    data = os.path.join(work, inst["name"])
    cmd = [game] + window_flags + [
        "-persistent-data-path", data,
        "-logFile", os.path.join(data, "player.log"),
    ]
    if inst["role"] == "host":
        cmd += ["-mp-autotest", "host", "-mp-song", scenario["song"], "-mp-rounds", "1",
                "-mp-players", str(len(scenario["instances"]))]
    else:
        cmd += ["-mp-autotest", "join", "127.0.0.1"]
    return cmd + inst["args"]


def run_instances(scenario, make_command, stagger, env=None, host_wait=HOST_WAIT, total_timeout=TOTAL_TIMEOUT,
                  popen_kwargs=None, on_started=None):
    """Starts the host, then the clients, and waits for all of them. Returns 0 when every instance exited with 0.
    popen_kwargs go to every Popen; on_started gets each new process."""
    procs = []
    try:
        for i, inst in enumerate(scenario["instances"]):
            if i == 1:
                time.sleep(host_wait)
            elif i > 1:
                time.sleep(stagger)
            print("starting " + inst["name"], flush=True)
            proc = subprocess.Popen(make_command(inst), env=env, **(popen_kwargs or {}))
            procs.append((inst["name"], proc))
            if on_started:
                on_started(proc)

        deadline = time.time() + total_timeout
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
    try:
        return run_instances(scenario, lambda inst: command(scenario, inst), args.stagger, env)
    finally:
        xvfb.kill()


if __name__ == "__main__":
    sys.exit(main())
