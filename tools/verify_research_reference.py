"""Compare C# smoke artifacts with independent RFC8785 and SciPy implementations."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
import rfc8785
from scipy import signal


def verify(path):
    artifacts = json.loads(path.read_text("utf-8"))
    for case in artifacts["jcs"]:
        value = rfc8785.dumps(json.loads(case["input"]))
        assert value.decode("utf-8") == case["canonical"]
        assert hashlib.sha256(value).hexdigest() == case["sha256"]
    maximum_error = 0.0
    for case in artifacts["filters"]:
        sos = [[s["b0"], s["b1"], s["b2"], 1, s["a1"], s["a2"]] for s in case["sections"]]
        reference = signal.butter(4, case["band"], btype="bandpass", output="sos", fs=case["rate"])
        frequencies = np.linspace(0, case["rate"] / 2, 4097)
        _, actual = signal.sosfreqz(sos, frequencies, fs=case["rate"])
        _, expected = signal.sosfreqz(reference, frequencies, fs=case["rate"])
        error = np.max(np.abs(actual - expected))
        assert error < 1e-10, f"Filter differs from SciPy: {error}"
        maximum_error = max(maximum_error, float(error))
    result = {"status": "PASS", "jcs_vectors": len(artifacts["jcs"]), "filter_cases": len(artifacts["filters"]),
              "max_complex_response_error": maximum_error}
    path.with_name("reference-verification.json").write_text(json.dumps(result, indent=2), "utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", type=Path)
    verify(parser.parse_args().artifacts)
