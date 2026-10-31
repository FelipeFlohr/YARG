"""Band scenarios (spec section 6.3). Standard library only."""


def _inst(name, role, profile, instrument, extra=(), difficulty="Expert"):
    return {
        "name": name,
        "role": role,
        "args": ["-mp-profile", profile, "-mp-instrument", instrument, "-mp-difficulty", difficulty] + list(extra),
    }


def _bot(name, profile, instrument, difficulty="Expert"):
    return _inst(name, "join", profile, instrument, ["-mp-bot", "1"], difficulty)


SCENARIOS = {
    "band4": {
        "song": "Soliloquy",
        "instances": [
            _inst("host", "host", "Guit", "FiveFretGuitar", ["-mp-miss", "30-40"]),
            _bot("bass", "Bass", "FiveFretBass"),
            _bot("drums", "Drum", "FiveLaneDrums"),  # Soliloquy has no ProDrums chart
            _bot("vocals", "Voc", "Vocals"),
        ],
        "expect": {"muted": {"Guitar": {"from": 30, "to": 40}}, "scripted": "Guit"},
    },
    "same-instrument": {
        "song": "Soliloquy",
        "instances": [
            _inst("host", "host", "Guit", "FiveFretGuitar", ["-mp-miss", "30-40"]),
            _bot("guit2", "Guit2", "FiveFretGuitar"),
        ],
        "expect": {"muted": {}, "scripted": "Guit"},
    },
    "keys": {
        "song": "Subdivisions",
        "instances": [
            _inst("host", "host", "Keys", "Keys", ["-mp-bot", "1"]),
            _inst("guitar", "join", "Guit", "FiveFretGuitar", ["-mp-miss", "60-70"]),
            _bot("prokeys", "ProKeys", "ProKeys"),
        ],
        "expect": {"muted": {"Guitar": {"from": 60, "to": 70}}, "scripted": "Guit"},
    },
    # Both guitarists miss, in windows that overlap from 35 to 40 s: the stem only mutes while both do
    "same-instrument-both-miss": {
        "song": "Soliloquy",
        "instances": [
            _inst("host", "host", "Guit", "FiveFretGuitar", ["-mp-miss", "30-40"]),
            _inst("guit2", "join", "Guit2", "FiveFretGuitar", ["-mp-miss", "35-45"]),
        ],
        "expect": {"muted": {"Guitar": {"from": 35, "to": 40}}, "scripted": ["Guit", "Guit2"]},
    },
    # Pro guitar and pro bass share the Guitar and Bass stems with nobody, 4-lane drums come from the pro chart.
    # Subdivisions has no 6-fret or elite drums chart, and no song in the test library does.
    "pro-instruments": {
        "song": "Subdivisions",
        "instances": [
            _inst("host", "host", "ProGuit", "ProGuitar_17Fret", ["-mp-bot", "1"]),
            _bot("probass", "ProBass", "ProBass_17Fret"),
            _bot("drums4", "Drum4", "FourLaneDrums"),
        ],
        "expect": {"muted": {}},
    },
    # Fail meter on: ten seconds of misses from one player must not end the song for a band of bots
    "fail-meter": {
        "song": "Soliloquy",
        "settings": {"NoFail": 0},
        "instances": [
            _inst("host", "host", "Guit", "FiveFretGuitar", ["-mp-miss", "30-40"]),
            _bot("bass", "Bass", "FiveFretBass"),
            _bot("drums", "Drum", "FiveLaneDrums"),
        ],
        "expect": {"muted": {"Guitar": {"from": 30, "to": 40}}, "scripted": "Guit"},
    },
    # The most machines a lobby takes
    "band8": {
        "song": "Subdivisions",
        "instances": [
            _inst("host", "host", "Guit", "FiveFretGuitar", ["-mp-miss", "60-70"]),
            _bot("bass", "Bass", "FiveFretBass"),
            _bot("drums", "Drum", "ProDrums"),
            _bot("vocals", "Voc", "Vocals"),
            _bot("harmony", "Harm", "Harmony"),
            _bot("keys", "Keys", "Keys"),
            _bot("prokeys", "ProKeys", "ProKeys"),
            _bot("probass", "ProBass", "ProBass_17Fret"),
        ],
        "expect": {"muted": {"Guitar": {"from": 60, "to": 70}}, "scripted": "Guit"},
    },
    "no-stems": {
        "song": "Making Memories",
        "instances": [
            _inst("host", "host", "Guit", "FiveFretGuitar", ["-mp-miss", "30-40"]),
            _bot("drums", "Drum", "ProDrums"),
        ],
        "expect": {"muted": {}, "scripted": "Guit"},
    },
}
