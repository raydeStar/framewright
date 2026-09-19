import importlib.util
import sys
import unittest
from pathlib import Path
from unittest import mock


SERVICE_PATH = Path(__file__).resolve().parents[1] / "yue2_service.py"
SPEC = importlib.util.spec_from_file_location("framewright_yue2_service", SERVICE_PATH)
assert SPEC and SPEC.loader
service = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = service
SPEC.loader.exec_module(service)


def request(**changes):
    value = {
        "id": "composition-123",
        "style": "cinematic synthwave, 108 BPM",
        "lyrics": "[Verse]\nCarry the signal home.",
        "cot": "full",
        "seed": 831001,
    }
    value.update(changes)
    return value


class YuE2WorkerContractTests(unittest.TestCase):
    def tearDown(self):
        with service.jobs_lock:
            service.jobs.clear()

    def test_request_ids_cannot_escape_worker_owned_output(self):
        service.validate_request(request(), require_abc=False)
        for unsafe in ("../escape", "folder/song", "C:\\song", "", "a" * 129):
            with self.subTest(unsafe=unsafe):
                with self.assertRaisesRegex(ValueError, "safe filename"):
                    service.validate_request(request(id=unsafe), require_abc=False)

    def test_seed_and_editable_abc_are_bounded(self):
        with self.assertRaisesRegex(ValueError, "seed"):
            service.validate_request(request(seed=True), require_abc=False)
        with self.assertRaisesRegex(ValueError, "V:"):
            service.validate_request(
                request(abc="X:1\nM:4/4\nK:C\nCDEF GABc CDEF GABc"),
                require_abc=True,
            )

    def test_health_probe_never_claims_a_missing_runtime_is_ready(self):
        with mock.patch.object(service.importlib.util, "find_spec", return_value=None):
            ready, detail = service.inspect_runtime()
        self.assertFalse(ready)
        self.assertIn("not installed", detail)

    def test_active_queue_is_capped_before_a_job_is_retained(self):
        with service.jobs_lock:
            for index in range(service.MAX_ACTIVE_JOBS):
                job = service.Job(f"{index:032x}", "compose", request())
                service.jobs[job.job_id] = job
        with self.assertRaisesRegex(RuntimeError, "queue is full"):
            service.submit("compose", request())
        self.assertEqual(service.MAX_ACTIVE_JOBS, len(service.jobs))


if __name__ == "__main__":
    unittest.main()
