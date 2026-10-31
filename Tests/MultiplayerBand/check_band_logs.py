"""Checks the [AutoTest] logs of a band scenario. Standard library only."""
import os
import re
import sys

PREFIX = "[AutoTest] "
PLAYING_RE = re.compile(r"^Playing as (\S+) bot=(\d)")
STEM_RE = re.compile(r"^Stem (\S+) fraction=(-?[\d.]+) song=(-?[\d.]+)")
RESULT_RE = re.compile(r"^Result: (\S+) (?:remote=\S+ )?score=(-?\d+) hit=(\d+)/(\d+)")
BAND_RE = re.compile(r"^Band: score=(-?\d+) stars=(\S+) players=(\d+)")

UNMUTE_SLACK = 5.0
MUTE_SLACK = 2.0
IGNORED_STEMS = ("Crowd",)


def parse_log(text):
    info = {"passed": False, "playing": None, "stems": [], "results": {}, "band": None}
    for line in text.splitlines():
        index = line.find(PREFIX)
        if index < 0:
            continue
        msg = line[index + len(PREFIX):].strip()
        if msg == "PASS":
            info["passed"] = True
            continue
        m = PLAYING_RE.match(msg)
        if m:
            info["playing"] = {"name": m.group(1), "bot": int(m.group(2))}
            continue
        m = STEM_RE.match(msg)
        if m:
            info["stems"].append((m.group(1), float(m.group(2)), float(m.group(3))))
            continue
        m = RESULT_RE.match(msg)
        if m:
            info["results"][m.group(1)] = (int(m.group(2)), int(m.group(3)), int(m.group(4)))
            continue
        m = BAND_RE.match(msg)
        if m:
            info["band"] = (int(m.group(1)), m.group(2), int(m.group(3)))
    return info


def _bot_flag(instance):
    args = instance.get("args", [])
    for i, a in enumerate(args):
        if a == "-mp-bot" and i + 1 < len(args):
            return args[i + 1] == "1"
    return False


def _check_stems(name, info, muted, errors):
    by_stem = {}
    for stem, fraction, song in info["stems"]:
        if stem in IGNORED_STEMS:
            continue
        by_stem.setdefault(stem, []).append((fraction, song))

    for stem in by_stem:
        if stem not in muted:
            errors.append("%s: unexpected events for stem %s" % (name, stem))

    for stem, window in muted.items():
        events = by_stem.get(stem, [])
        lo, hi = window["from"], window["to"]
        first_zero = next((song for fraction, song in events if fraction == 0.0), None)
        if first_zero is not None and not (lo <= first_zero <= hi + MUTE_SLACK):
            errors.append("%s: stem %s first muted at %s, outside %s..%s" % (name, stem, first_zero, lo, hi + MUTE_SLACK))
        mute_at = None
        for i, (fraction, song) in enumerate(events):
            if fraction == 0.0 and lo <= song <= hi + MUTE_SLACK:
                mute_at = i
                break
        if mute_at is None:
            errors.append("%s: stem %s was never muted between %s and %s" % (name, stem, lo, hi + MUTE_SLACK))
            continue
        unmuted = any(f == 1.0 and s <= hi + UNMUTE_SLACK for f, s in events[mute_at + 1:])
        if not unmuted:
            errors.append("%s: stem %s did not come back to 1.00 by %s" % (name, stem, hi + UNMUTE_SLACK))
        if events[-1][0] != 1.0:
            errors.append("%s: stem %s ends at fraction %.2f instead of 1.00" % (name, stem, events[-1][0]))


def check(scenario, logs):
    errors = []
    expect = scenario.get("expect", {})
    muted = expect.get("muted", {})
    parsed = {name: parse_log(text) for name, text in logs.items()}
    instances = {i["name"]: i for i in scenario.get("instances", [])}

    for name, info in parsed.items():
        if not info["passed"]:
            errors.append("%s: no PASS line" % name)
        inst = instances.get(name)
        if inst is not None and _bot_flag(inst):
            if info["playing"] is None or info["playing"]["bot"] != 1:
                errors.append("%s: expected bot=1 in the Playing as line" % name)
        _check_stems(name, info, muted, errors)

    names = sorted(parsed)
    if names:
        ref = names[0]
        for name in names[1:]:
            if parsed[name]["results"] != parsed[ref]["results"]:
                errors.append("results differ between %s and %s: %s vs %s" %
                              (ref, name, parsed[ref]["results"], parsed[name]["results"]))
            if parsed[name]["band"] != parsed[ref]["band"]:
                errors.append("band differs between %s and %s: %s vs %s" %
                              (ref, name, parsed[ref]["band"], parsed[name]["band"]))
        if not parsed[ref]["results"]:
            errors.append("%s: no Result lines" % ref)
        if parsed[ref]["band"] is None:
            errors.append("%s: no Band line" % ref)

        scripted = expect.get("scripted") or []
        for player in [scripted] if isinstance(scripted, str) else scripted:
            result = parsed[ref]["results"].get(player)
            if result is None:
                errors.append("scripted player %s has no Result line" % player)
            elif result[1] >= result[2]:
                errors.append("scripted player %s hit %d/%d, expected misses" % (player, result[1], result[2]))
    return errors


def main(argv=None):
    from scenarios import SCENARIOS

    argv = sys.argv[1:] if argv is None else argv
    if len(argv) != 2 or argv[0] not in SCENARIOS:
        print("usage: check_band_logs.py <scenario> <folder with <instance>.log>")
        print("scenarios: " + ", ".join(sorted(SCENARIOS)))
        return 2
    scenario = SCENARIOS[argv[0]]
    logs = {}
    errors = []
    for inst in scenario["instances"]:
        path = os.path.join(argv[1], inst["name"] + ".log")
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                logs[inst["name"]] = f.read()
        except OSError as e:
            errors.append("%s: cannot read log (%s)" % (inst["name"], e))
    errors += check(scenario, logs)
    for e in errors:
        print("FAIL: " + e)
    if not errors:
        print("OK: %s" % argv[0])
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
