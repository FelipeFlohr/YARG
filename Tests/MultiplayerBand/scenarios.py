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
            _bot("vocals", "Voc", "Vocals", "Beginner"),  # the vocals chart only reports Beginner
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
    "no-stems": {
        "song": "Making Memories",
        "instances": [
            _inst("host", "host", "Guit", "FiveFretGuitar", ["-mp-miss", "30-40"]),
            _bot("drums", "Drum", "ProDrums"),
        ],
        "expect": {"muted": {}, "scripted": "Guit"},
    },
}
