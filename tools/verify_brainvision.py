"""Read a Studio BrainVision export with MNE and compare against the raw binary.

python tools/verify_brainvision.py SESSION_DIRECTORY EXPORT_DIRECTORY
Requires requirements-research-qa.txt. This checks file/units/event fidelity, not
physical stimulus timing, the vendor's calibration, or recognition accuracy.
"""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import struct

import mne
import numpy as np


def crc32c(data):
    crc = 0xFFFFFFFF
    for byte in data:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ (0x82F63B78 if crc & 1 else 0)
    return crc ^ 0xFFFFFFFF


def raw_samples(path):
    """Independent decoder, intentionally not the C# reader/exporter."""
    with path.open("rb") as stream:
        assert stream.read(8) == b"SSVEPB01", "Unknown raw format"
        size, = struct.unpack("<i", stream.read(4))
        header = json.loads(stream.read(size))
        assert header["version"] == 1 and header["numericType"] == "float64"
        yield header
        channels = header["channelCount"]
        stride = 25 + 8 * channels
        while block_header := stream.read(12):
            assert len(block_header) == 12, "Truncated block header"
            magic, size, checksum = struct.unpack("<IiI", block_header)
            assert magic == 0x314B4C42 and 0 < size <= 16777216
            body = stream.read(size)
            assert len(body) == size and crc32c(body) == checksum, "Incomplete or corrupt source block"
            metadata_size, = struct.unpack_from("<i", body)
            metadata = json.loads(body[4:4 + metadata_size])
            offset = 4 + metadata_size
            assert len(body) - offset == metadata["count"] * stride
            for row in range(metadata["count"]):
                _, _, lost, bitmap = struct.unpack_from("<qd?Q", body, offset)
                values = np.frombuffer(body, dtype="<f8", count=channels, offset=offset + 25)
                valid = not lost and np.all(np.isfinite(values))
                expected_bitmap = sum(1 << c for c, value in enumerate(values) if not lost and math.isfinite(value))
                assert bitmap == expected_bitmap, "Validity bitmap differs from raw samples"
                if valid:
                    yield metadata, metadata["firstSampleIndex"] + row, values
                offset += stride


def verify(source, export):
    assert not (export / "EXPORT-INCOMPLETE.txt").exists(), "Export is incomplete"
    manifest = json.loads((export / "export-manifest.json").read_text("utf-8"))
    source_manifest = json.loads((source / "manifest.json").read_text("utf-8"))
    assert manifest["sourceSessionId"] == source_manifest["sessionId"]
    rows = raw_samples(source / "samples.bin")
    header = next(rows)
    assert header["runConfigHash"] == manifest["runConfigHash"]
    with (export / "channels.tsv").open(encoding="utf-8", newline="") as stream:
        names = [row["name"] for row in csv.DictReader(stream, delimiter="\t")]
    with (export / "events.tsv").open(encoding="utf-8", newline="") as stream:
        events = list(csv.DictReader(stream, delimiter="\t"))
    placed = set()
    reports = []
    for segment in manifest["segments"]:
        raw = mne.io.read_raw_brainvision(export / segment["headerFile"], preload=False, verbose="ERROR")
        assert raw.ch_names == names, "Channel names/order mismatch"
        assert raw.info["sfreq"] == header["samplingRateHz"] == manifest["samplingRateHz"]
        assert raw.n_times == segment["sampleCount"] == segment["lastSampleIndex"] - segment["firstSampleIndex"] + 1
        maximum_error = 0.0
        for start in range(0, raw.n_times, 4096):
            stop = min(raw.n_times, start + 4096)
            expected = []
            for position in range(start, stop):
                info, index, values = next(rows)
                assert info["clockEpoch"] == segment["clockEpoch"] and info["runGeneration"] == segment["run"]
                assert info["recognitionEnabled"] == segment["recognitionEnabled"]
                assert index == segment["firstSampleIndex"] + position, "Gap was padded or valid data omitted"
                expected.append(values * header["scaleToVolts"])
            expected = np.array(expected).T
            actual = raw.get_data(start=start, stop=stop)
            error = np.abs(actual - expected)
            assert np.all(error <= np.maximum(1e-15, np.abs(expected) * 2e-7)), "Amplitude/unit mismatch"
            maximum_error = max(maximum_error, float(error.max()))
        expected_events = []
        for event_id, event in enumerate(events):
            if event_id in placed or event["written_to_vmrk"] != "True":
                continue
            x = float(event["estimated_sample"])
            index = math.floor(x + .5) if x >= 0 else math.ceil(x - .5)
            if segment["firstSampleIndex"] <= index <= segment["lastSampleIndex"]:
                description = f'Comment/{event["trial_id"]}:{event["phase"]}:{event["target"] or event["label"]}'
                expected_events.append(((index - segment["firstSampleIndex"]) / raw.info["sfreq"], description))
                placed.add(event_id)
        assert len(raw.annotations) == len(expected_events) == segment["eventCount"], "Event count mismatch"
        for annotation, expected_event in zip(raw.annotations, sorted(expected_events)):
            assert abs(annotation["onset"] - expected_event[0]) < 1e-9, "Event offset mismatch"
            assert annotation["description"] == expected_event[1], "Event description mismatch"
        reports.append({"file": segment["headerFile"], "samples": int(raw.n_times), "channels": len(names),
                        "events": len(expected_events), "max_absolute_error_volts": maximum_error})
        raw.close()
    assert next(rows, None) is None, "Valid raw samples missing from export"
    assert len(placed) == sum(event["written_to_vmrk"] == "True" for event in events)
    result = {"status": "PASS", "mne_version": mne.__version__, "source_session_id": manifest["sourceSessionId"],
              "segments": reports, "total_samples": sum(item["samples"] for item in reports),
              "export_manifest_sha256": hashlib.sha256((export / "export-manifest.json").read_bytes()).hexdigest(),
              "limitation": "File fidelity only; physical timing and participant accuracy not validated."}
    (export / "mne-readback.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), "utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("export", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.source, args.export), ensure_ascii=False, indent=2))
