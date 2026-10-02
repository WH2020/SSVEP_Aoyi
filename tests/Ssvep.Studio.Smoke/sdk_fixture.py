"""SDK bridge protocol fixture. Synthetic samples only; never connects to hardware."""
import argparse
import json
import math
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src/Ssvep.Speller.App/Bridges"))
from synchroni_eeg_bridge import ControlChannel
health = ControlChannel()
health.set_state("ready")

p = argparse.ArgumentParser()
p.add_argument("--device-address", default="good")
args, _ = p.parse_known_args()
mode = args.device_address or "good"
rate = 500 if mode == "500" else 250
channel_count = 32 if mode == "32" else 8
print(json.dumps({"type": "meta", "deviceName": "SDK TEST FIXTURE", "deviceAddress": "fixture", "samplingRateHz": rate, "channelCount": channel_count, "sampleUnit": "uV", "scaleToVolts": 1e-6, "unitSource": "synthetic-fixture"}), flush=True)
start = time.monotonic()
index = 0
triggered = False
last_trigger = ""
index_offset = 0
control = os.environ.get("SSVEP_FIXTURE_CONTROL", "")
while True:
    elapsed = time.monotonic() - start
    trigger = open(control).read() if control and os.path.exists(control) else ""
    if trigger and trigger != last_trigger:
        last_trigger = trigger
        triggered = True
        if mode == "format-pause":
            rate = 500
            print(json.dumps({"type": "meta", "deviceName": "SYNTHETIC FORMAT CHANGE", "samplingRateHz": rate, "channelCount": 8}), flush=True)
        elif mode in ("sensor-error", "error", "explicit-disconnect", "enum-disconnect"):
            if mode.endswith("disconnect"):
                event = {"type": "state", "state": "DeviceStateEx.Disconnected" if mode == "enum-disconnect" else "disconnected"}
            else:
                event = {"type": "sensor_error" if mode == "sensor-error" else "error", "message": mode + " synthetic fault while stdout continues"}
            print(json.dumps(event), file=sys.stderr, flush=True)
        elif mode in ("loss", "loss-gap", "loss-repeat"):
            for _ in range(2 if mode == "loss-repeat" else 1):
                lost_count = 100 if mode == "loss-gap" else 10
                print(json.dumps({"type": "sensor_error", "message": f"MSG|LOST SAMPLE|MAC|AA:BB|TYPE|16|COUNT|{lost_count}"}), file=sys.stderr, flush=True)
                if lost_count < 100:
                    for _ in range(lost_count):
                        print(json.dumps({"type": "sample", "sampleIndex": index, "hostReceivedUnixMs": int(time.time() * 1000), "samplingRateHz": rate, "channelCount": channel_count, "channels": [0] * channel_count, "lost": True, "impedance": [100000] * channel_count}))
                        index += 1
                else:
                    index += lost_count
                index_offset += lost_count
            sys.stdout.flush()
        elif mode == "impedance-pulse":
            for impedance in (600000, 100000):
                print(json.dumps({"type": "sample", "sampleIndex": index, "hostReceivedUnixMs": int(time.time() * 1000), "samplingRateHz": rate, "channelCount": 8, "channels": [0] * 8, "impedance": [impedance] * 8}))
                index += 1
            sys.stdout.flush()
        with open(control + ".sent", "w") as marker:
            marker.write("synthetic event sent; stdout stays alive")
    if mode == "disconnect" and elapsed > 4:
        sys.exit(7)
    target = index_offset + int(elapsed * rate)
    while index < target:
        values = [math.sin(2 * math.pi * 9.2 * index / rate + c * .08) + .35 * math.sin(4 * math.pi * 9.2 * index / rate + c * .08) for c in range(channel_count)]
        item = {"type": "sample", "sampleIndex": index, "hostReceivedUnixMs": int(time.time() * 1000), "samplingRateHz": rate, "channelCount": channel_count, "channels": values, "lost": False}
        if mode != "no-impedance" and not (mode == "stale" and elapsed > 3):
            item["impedance"] = [0 if mode == "unknown-impedance" else 600000 if mode == "high" else 100000] * channel_count
        print(json.dumps(item, separators=(",", ":")))
        index += 1
    sys.stdout.flush()
    health.last_callback_ns = time.perf_counter_ns()
    health.last_sample_index = index - 1
    time.sleep(.02)
