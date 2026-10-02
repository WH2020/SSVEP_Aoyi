#!/usr/bin/env python
import argparse
import json
import os
import queue
import select
import signal
import socket
import sys
import threading
import time
import uuid


def emit_stdout(payload):
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def emit_stderr(payload):
    print(json.dumps(payload, separators=(",", ":")), file=sys.stderr, flush=True)


class ControlChannel:
    """Independent loopback control transport; sample serialization cannot starve heartbeat/pong."""
    def __init__(self):
        self.instance = uuid.uuid4().hex
        self.last_callback_ns = None
        self.last_sample_index = None
        self.state = "starting"
        self.state_changed_ns = time.perf_counter_ns()
        self.stopped = threading.Event()
        self.enabled = bool(os.environ.get("SSVEP_CONTROL_PORT"))
        self.thread = None
        if self.enabled:
            self.thread = threading.Thread(target=self._run, name="ssvep-control", daemon=True)
            self.thread.start()

    def set_state(self, value):
        self.state = str(value)
        self.state_changed_ns = time.perf_counter_ns()

    def _run(self):
        try:
            with socket.create_connection(("127.0.0.1", int(os.environ["SSVEP_CONTROL_PORT"])), timeout=2) as conn:
                conn.settimeout(1)
                def send(data):
                    conn.sendall((json.dumps(data, separators=(",", ":")) + "\n").encode("utf-8"))
                send({"type": "hello", "protocolVersion": 2, "token": os.environ["SSVEP_CONTROL_TOKEN"], "bridgeInstanceId": self.instance})
                pending = b""
                sequence = 0
                next_heartbeat = 0.0
                while not self.stopped.is_set():
                    now = time.monotonic()
                    if now >= next_heartbeat:
                        sequence += 1
                        send({"type": "heartbeat", "protocolVersion": 2, "bridgeInstanceId": self.instance, "sequence": sequence,
                              "pythonSeconds": time.perf_counter_ns() / 1e9,
                              "lastCallbackSeconds": None if self.last_callback_ns is None else self.last_callback_ns / 1e9,
                              "lastSampleIndex": self.last_sample_index, "sdkState": self.state,
                              "stateChangedSeconds": self.state_changed_ns / 1e9})
                        next_heartbeat = now + 1
                    if not select.select([conn], [], [], .05)[0]:
                        continue
                    part = conn.recv(4096)
                    receive_ns = time.perf_counter_ns()
                    if not part:
                        return
                    pending += part
                    if len(pending) > 65536:
                        raise ValueError("control frame too large")
                    while b"\n" in pending:
                        line, pending = pending.split(b"\n", 1)
                        request = json.loads(line)
                        if request.get("type") == "ping":
                            send({"type": "pong", "sequence": request["sequence"], "hostSend": request["hostSend"],
                                  "pythonReceive": receive_ns / 1e9, "pythonSend": time.perf_counter_ns() / 1e9})
        except Exception as exc:
            emit_stderr({"type": "control_error", "message": str(exc)})

    def close(self):
        self.stopped.set()
        if self.thread is not None:
            self.thread.join(timeout=2)


class BatchOutput:
    def __init__(self):
        self.queue = queue.Queue(maxsize=512)
        self.failure = None
        self.thread = threading.Thread(target=self._run, name="ssvep-samples", daemon=True)
        self.thread.start()

    def put(self, payload):
        try:
            self.queue.put_nowait(payload)
        except queue.Full:
            self.failure = "SDK sample queue overflow"
            emit_stderr({"type": "error", "message": self.failure})

    def _run(self):
        try:
            while True:
                payload = self.queue.get()
                if payload is None:
                    return
                emit_stdout(payload)
        except Exception as exc:
            self.failure = str(exc)

    def close(self):
        try:
            self.queue.put(None, timeout=1)
        except queue.Full:
            return
        self.thread.join(timeout=2)


def main():
    # Windows may otherwise choose a legacy code page for redirected streams.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")
    parser = argparse.ArgumentParser(description="以 NDJSON 输出 Synchroni SDK 脑电样本。")
    parser.add_argument("--list-devices", action="store_true")
    parser.add_argument("--scan-ms", type=int, default=6000)
    parser.add_argument("--device-address", default=None)
    parser.add_argument("--device-name-prefix", default=None)
    parser.add_argument("--package-count", type=int, default=10)
    parser.add_argument("--power-refresh-ms", type=int, default=60000)
    parser.add_argument("--filter-50hz", action="store_true")
    parser.add_argument("--filter-hpf", action="store_true")
    parser.add_argument("--filter-lpf", action="store_true")
    parser.add_argument("--batch-protocol", action="store_true")
    args = parser.parse_args()

    try:
        from sensor import DataType, DeviceStateEx, SensorController
    except Exception as exc:
        emit_stderr({"type": "error", "message": f"导入 synchroni-sensor-sdk 失败：{exc}"})
        return 2

    control_channel = ControlChannel()
    batch_output = BatchOutput() if args.batch_protocol else None
    controller = SensorController()
    sensor = None
    running = True
    batch_id = 0

    def stop(_signal=None, _frame=None):
        nonlocal running
        running = False

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    def select_device(devices):
        if args.device_address:
            for device in devices:
                if device.Address.lower() == args.device_address.lower():
                    return device
            return None

        candidates = devices
        if args.device_name_prefix:
            candidates = [device for device in devices if device.Name.startswith(args.device_name_prefix)]
        else:
            candidates = [
                device
                for device in devices
                if device.Name.startswith("OB") or device.Name.startswith("Sync") or device.Name.startswith("Orion")
            ] or devices

        return candidates[0] if candidates else None

    def on_data(_sensor, data):
        received_ns = time.perf_counter_ns()
        received_unix_ms = int(time.time() * 1000)
        nonlocal batch_id
        if data is None or data.dataType != DataType.NTF_EEG or not data.channelSamples:
            return

        channel_count = len(data.channelSamples)
        package_count = max((len(channel) for channel in data.channelSamples), default=0)
        host_received_unix_ms = received_unix_ms
        control_channel.last_callback_ns = received_ns
        batch_id += 1
        samples = []

        for sample_offset in range(package_count):
            channels = []
            impedance = []
            lost = False
            sample_index = None
            device_elapsed_ms = None

            for channel_index, channel_samples in enumerate(data.channelSamples):
                if sample_offset >= len(channel_samples):
                    channels.append(0.0)
                    impedance.append(0)
                    lost = True
                    continue

                sample = channel_samples[sample_offset]
                channels.append(float(sample.data))
                impedance.append(int(getattr(sample, "impedance", 0)))
                lost = lost or bool(getattr(sample, "isLost", False))
                if sample_index is None:
                    sample_index = int(getattr(sample, "sampleIndex", sample_offset))
                sdk_timestamp = int(getattr(sample, "timeStampInMs", 0))
                if sdk_timestamp >= 0:
                    device_elapsed_ms = sdk_timestamp

            payload = (
                {
                    "type": "sample",
                    "sampleIndex": sample_index if sample_index is not None else sample_offset,
                    "deviceElapsedMs": device_elapsed_ms,
                    "hostReceivedUnixMs": host_received_unix_ms,
                    "samplingRateHz": int(getattr(data, "sampleRate", 250) or 250),
                    "channelCount": channel_count,
                    "channels": channels,
                    "lost": lost,
                    "impedance": impedance,
                }
            )
            control_channel.last_sample_index = payload["sampleIndex"]
            if batch_output is None:
                emit_stdout(payload)
            else:
                samples.append(payload)
        if batch_output is not None:
            batch_output.put({"type": "sample-batch", "protocolVersion": 2,
                              "bridgeInstanceId": control_channel.instance, "batchId": batch_id,
                              "receivedPerfNs": received_ns, "hostReceivedUnixMs": received_unix_ms,
                              "samplingRateHz": int(getattr(data, "sampleRate", 250) or 250),
                              "channelCount": channel_count, "samples": samples})

    def on_state(_sensor, state):
        control_channel.set_state(state)
        emit_stderr({"type": "state", "state": str(state)})

    try:
        devices = controller.scan(args.scan_ms)
        if args.list_devices:
            for device in devices:
                if args.device_name_prefix and not device.Name.startswith(args.device_name_prefix):
                    continue
                emit_stdout({"type": "device", "name": device.Name, "address": device.Address})
            return 0

        device = select_device(devices)
        if device is None:
            emit_stderr({"type": "error", "message": "未找到 Synchroni 蓝牙设备"})
            return 3

        sensor = controller.requireSensor(device)
        if sensor is None:
            emit_stderr({"type": "error", "message": "创建传感器配置失败"})
            return 4

        sensor.onDataCallback = on_data
        sensor.onErrorCallback = lambda _sensor, reason: emit_stderr({"type": "sensor_error", "message": str(reason)})
        sensor.onStateChanged = on_state
        sensor.onPowerChanged = lambda _sensor, power: emit_stderr({"type": "battery", "power": int(power)})

        if sensor.deviceState != DeviceStateEx.Ready and not sensor.connect():
            emit_stderr({"type": "error", "message": f"连接设备失败：{device.Name}"})
            return 5

        sensor.setParam("NTF_EEG", "ON")
        sensor.setParam("NTF_EMG", "OFF")
        sensor.setParam("NTF_ECG", "OFF")
        sensor.setParam("NTF_IMU", "OFF")
        sensor.setParam("NTF_BRTH", "OFF")
        if args.filter_50hz:
            sensor.setParam("FILTER_50HZ", "ON")
        if args.filter_hpf:
            sensor.setParam("FILTER_HPF", "ON")
        if args.filter_lpf:
            sensor.setParam("FILTER_LPF", "ON")

        if not sensor.hasInited and not sensor.init(args.package_count, args.power_refresh_ms):
            emit_stderr({"type": "error", "message": f"初始化设备失败：{device.Name}"})
            return 6

        device_info = sensor.getDeviceInfo()
        sampling_rate = int(getattr(device_info, "EegSampleRate", 250) or 250)
        channel_count = int(getattr(device_info, "EegChannelCount", 8) or 8)
        emit_stdout(
            {
                "type": "meta",
                "deviceName": device.Name,
                "deviceAddress": device.Address,
                "samplingRateHz": sampling_rate,
                "channelCount": channel_count,
                "sampleUnit": "uV",
                "scaleToVolts": 1e-6,
                "unitSource": "Synchroni SensorData sample.data contract (sensor_data.py)",
            }
        )

        if not sensor.startDataNotification():
            emit_stderr({"type": "error", "message": f"启动脑电数据通知失败：{device.Name}"})
            return 7

        while running:
            if batch_output is not None and batch_output.failure:
                return 8
            time.sleep(0.2)

        return 0
    finally:
        try:
            if sensor is not None:
                if sensor.isDataTransfering:
                    sensor.stopDataNotification()
                sensor.disconnect()
            controller.terminate()
        except Exception as exc:
            emit_stderr({"type": "shutdown_error", "message": str(exc)})
        if batch_output is not None:
            batch_output.close()
        control_channel.close()


if __name__ == "__main__":
    sys.exit(main())
