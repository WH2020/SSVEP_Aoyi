#!/usr/bin/env python
import argparse
import json
import signal
import sys
import time


def emit_stdout(payload):
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def emit_stderr(payload):
    print(json.dumps(payload, separators=(",", ":")), file=sys.stderr, flush=True)


def main():
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
    args = parser.parse_args()

    try:
        from sensor import DataType, DeviceStateEx, SensorController
    except Exception as exc:
        emit_stderr({"type": "error", "message": f"导入 synchroni-sensor-sdk 失败：{exc}"})
        return 2

    controller = SensorController()
    sensor = None
    running = True

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
        if data is None or data.dataType != DataType.NTF_EEG or not data.channelSamples:
            return

        channel_count = len(data.channelSamples)
        package_count = max((len(channel) for channel in data.channelSamples), default=0)
        host_received_unix_ms = int(time.time() * 1000)

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

            emit_stdout(
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
        sensor.onStateChanged = lambda _sensor, state: emit_stderr({"type": "state", "state": str(state)})
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
            }
        )

        if not sensor.startDataNotification():
            emit_stderr({"type": "error", "message": f"启动脑电数据通知失败：{device.Name}"})
            return 7

        while running:
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


if __name__ == "__main__":
    sys.exit(main())
