import unittest

import check_band_logs as c

SCENARIO = {
    "instances": [
        {"name": "host", "role": "host", "args": ["-mp-profile", "Guit", "-mp-miss", "30-40"]},
        {"name": "bass", "role": "join", "args": ["-mp-profile", "Bass", "-mp-bot", "1"]},
    ],
    "expect": {"muted": {"Guitar": {"from": 30, "to": 40}}, "scripted": "Guit"},
}


def make_log(bot=0, guit_hit=90, bass_score=2000, band="score=3000 stars=4 players=2", passed=True,
             stems=None, remote=0):
    if stems is None:
        stems = [("Guitar", "0.00", "30.50"), ("Guitar", "1.00", "40.60")]
    lines = [
        "[AutoTest] Playing as Guit bot=%d instrument=FiveFretGuitar difficulty=Expert" % bot,
        "[INFO] unrelated noise",
    ]
    for stem, fraction, song in stems:
        lines.append("[AutoTest] Stem %s fraction=%s song=%s" % (stem, fraction, song))
    lines.append("[AutoTest] Result: Guit remote=%d score=1000 hit=%d/100 sp=1" % (remote, guit_hit))
    lines.append("[AutoTest] Result: Bass remote=%d score=%d hit=100/100 sp=0" % (1 - remote, bass_score))
    lines.append("[AutoTest] Band: " + band)
    if passed:
        lines.append("[AutoTest] PASS")
    return "\n".join(lines) + "\n"


def logs(**kw):
    return {"host": make_log(**kw), "bass": make_log(bot=1, remote=1, **kw)}


class CheckTests(unittest.TestCase):
    def test_passes_when_everything_matches(self):
        self.assertEqual(c.check(SCENARIO, logs()), [])

    def test_fails_on_score_mismatch(self):
        l = logs()
        l["bass"] = make_log(bot=1, remote=1, bass_score=2001)
        self.assertTrue(any("results" in e for e in c.check(SCENARIO, l)))

    def test_fails_on_band_mismatch(self):
        l = logs()
        l["bass"] = make_log(bot=1, remote=1, band="score=3001 stars=4 players=2")
        self.assertTrue(any("band" in e for e in c.check(SCENARIO, l)))

    def test_fails_on_player_count_mismatch(self):
        l = logs()
        l["bass"] = make_log(bot=1, remote=1, band="score=3000 stars=4 players=1")
        self.assertTrue(any("band" in e for e in c.check(SCENARIO, l)))

    def test_fails_when_band_line_has_no_player_count(self):
        l = logs()
        l["host"] = make_log(band="score=3000 stars=4")
        l["bass"] = make_log(bot=1, remote=1, band="score=3000 stars=4")
        self.assertTrue(any("no Band line" in e for e in c.check(SCENARIO, l)))

    def test_fails_when_a_machine_never_muted(self):
        l = logs()
        l["bass"] = make_log(bot=1, remote=1, stems=[])
        errors = c.check(SCENARIO, l)
        self.assertTrue(any("bass" in e and "Guitar" in e for e in errors))

    def test_fails_when_stem_stays_muted(self):
        l = logs()
        l["host"] = make_log(stems=[("Guitar", "0.00", "30.50")])
        self.assertTrue(any("host" in e and "Guitar" in e for e in c.check(SCENARIO, l)))

    def test_fails_when_unmute_is_too_late(self):
        l = logs()
        l["host"] = make_log(stems=[("Guitar", "0.00", "30.50"), ("Guitar", "1.00", "45.10")])
        self.assertTrue(any("host" in e for e in c.check(SCENARIO, l)))

    def test_fails_on_unexpected_stem_event(self):
        l = logs()
        l["host"] = make_log(stems=[("Guitar", "0.00", "30.50"), ("Guitar", "1.00", "40.60"),
                                    ("Bass", "0.00", "31.00")])
        self.assertTrue(any("Bass" in e for e in c.check(SCENARIO, l)))

    def test_crowd_events_are_ignored(self):
        l = logs()
        l["host"] = make_log(stems=[("Guitar", "0.00", "30.50"), ("Crowd", "0.50", "35.00"),
                                    ("Guitar", "1.00", "40.60")])
        self.assertEqual(c.check(SCENARIO, l), [])

    def test_fails_without_pass(self):
        l = logs()
        l["host"] = make_log(passed=False)
        self.assertTrue(any("PASS" in e for e in c.check(SCENARIO, l)))

    def test_scripted_player_must_miss(self):
        l = {"host": make_log(guit_hit=100), "bass": make_log(bot=1, remote=1, guit_hit=100)}
        self.assertTrue(any("scripted" in e for e in c.check(SCENARIO, l)))

    def test_bot_instance_must_log_bot_1(self):
        l = logs()
        l["bass"] = make_log(bot=0, remote=1)
        self.assertTrue(any("bot" in e for e in c.check(SCENARIO, l)))

    def test_no_muted_means_no_events(self):
        sc = {"instances": SCENARIO["instances"][:1],
              "expect": {"muted": {}, "scripted": "Guit"}}
        self.assertEqual(c.check(sc, {"host": make_log(stems=[])}), [])
        self.assertTrue(c.check(sc, {"host": make_log()}))

    def test_fails_on_spurious_mute_before_window(self):
        stems = [("Guitar", "0.00", "10.00"), ("Guitar", "1.00", "11.00"),
                 ("Guitar", "0.00", "30.50"), ("Guitar", "1.00", "40.60")]
        l = logs()
        l["host"] = make_log(stems=stems)
        self.assertTrue(any("host" in e and "outside" in e for e in c.check(SCENARIO, l)))


if __name__ == "__main__":
    unittest.main()
