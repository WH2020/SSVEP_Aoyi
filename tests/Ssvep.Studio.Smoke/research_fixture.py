"""Version-2 batch/control fixture. Synthetic data; never imports or connects an EEG SDK."""
import argparse
import json
import math
import os
import select
import socket
import sys
import threading
import time
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--device-address", default="batch-good")
args, _ = parser.parse_known_args()
mode = args.device_address
instance = uuid.uuid4().hex
state = {"last": None, "index": None, "triggered": False}
trigger = os.environ.get("SSVEP_FIXTURE_CONTROL", "")


def emit(value):
    print(json.dumps(value, separators=(",", ":")), flush=True)


def control():
    with socket.create_connection(("127.0.0.1", int(os.environ["SSVEP_CONTROL_PORT"]))) as conn:
        def send(data):
            conn.sendall((json.dumps(data, separators=(",", ":")) + "\n").encode())
        send({"type": "hello", "protocolVersion": 2, "bridgeInstanceId": instance, "token": os.environ["SSVEP_CONTROL_TOKEN"]})
        pending = b""
        sequence = 0
        next_heartbeat = 0
        while True:
            if time.monotonic() >= next_heartbeat:
                sequence += 1
                if not (state["triggered"] and mode == "batch-heartbeat"):
                    send({"type": "heartbeat", "protocolVersion": 2, "bridgeInstanceId": instance, "sequence": sequence,
                          "pythonSeconds": time.perf_counter(), "lastCallbackSeconds": state["last"],
                          "lastSampleIndex": state["index"], "sdkState": "ready", "stateChangedSeconds": 0})
                next_heartbeat = time.monotonic() + .25
            if select.select([conn], [], [], .01)[0]:
                value = conn.recv(4096)
                p2 = time.perf_counter()
                if not value:
                    return
                pending += value
                while b"\n" in pending:
                    line, pending = pending.split(b"\n", 1)
                    ping = json.loads(line)
                    send({"type": "pong", "sequence": ping["sequence"], "hostSend": ping["hostSend"],
                          "pythonReceive": p2, "pythonSend": time.perf_counter()})


threading.Thread(target=control, daemon=True).start()
rate = 500 if mode == "batch-500" else 250
channels = 32 if mode == "batch-500" else 8
emit({"type": "meta", "deviceName": "SYNTHETIC RESEARCH FIXTURE", "deviceAddress": "fixture",
      "samplingRateHz": rate, "channelCount": channels, "sampleUnit": "uV", "scaleToVolts": 1e-6, "unitSource": "synthetic-fixture"})
start = time.perf_counter()
index = 0
batch = 0
while True:
    if trigger and os.path.exists(trigger) and not state["triggered"]:
        state["triggered"] = True
        if mode == "batch-exit":
            sys.exit(9)
        if mode == "batch-disconnect":
            print(json.dumps({"type": "state", "state": "disconnected"}), file=sys.stderr, flush=True)
    if state["triggered"] and mode == "batch-no-data":
        time.sleep(.02)
        continue
    received_ns = time.perf_counter_ns()
    utc_ms = int(time.time()*1000)
    target = int((received_ns/1e9-start)*rate)
    samples = []
    while index < target:
        values = [20*(math.sin(2*math.pi*9.2*index/rate+c*.08)+.3*math.sin(4*math.pi*9.2*index/rate+c*.08)) for c in range(channels)]
        samples.append({"type": "sample", "sampleIndex": index, "hostReceivedUnixMs": utc_ms,
                        "deviceElapsedMs": index*1000/rate, "samplingRateHz": rate, "channelCount": channels,
                        "channels": values, "lost": False})
        index += 1
    if samples:
        batch += 1
        state["last"] = received_ns/1e9
        state["index"] = index-1
        emit({"type": "sample-batch", "protocolVersion": 2, "bridgeInstanceId": instance, "batchId": batch,
              "receivedPerfNs": received_ns, "hostReceivedUnixMs": utc_ms, "samplingRateHz": rate,
              "channelCount": channels, "samples": samples})
    time.sleep(.02)
