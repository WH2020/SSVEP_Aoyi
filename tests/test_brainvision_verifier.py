"""Run with the MNE environment from tools/requirements-research-qa.txt."""
import unittest

from tools.verify_brainvision import verify_annotations


class BoundaryAnnotationTests(unittest.TestCase):
    def setUp(self):
        self.observed = [
            {"onset": 4.0, "description": "Comment/t:observe-end:T1"},
            {"onset": 4.0, "description": "Comment/t:deciding-start:T1"},
        ]
        self.expected = [
            (4.0, "Comment/t:deciding-start:T1"),
            (4.0, "Comment/t:observe-end:T1"),
        ]

    def test_same_sample_annotations_do_not_require_alphabetical_file_order(self):
        verify_annotations(self.observed, self.expected)

    def test_missing_and_duplicate_events_fail(self):
        for values in (self.observed[:1], self.observed + [self.observed[0]]):
            with self.subTest(values=values), self.assertRaisesRegex(AssertionError, "count"):
                verify_annotations(values, self.expected)

    def test_changed_event_description_fails(self):
        with self.assertRaisesRegex(AssertionError, "description"):
            verify_annotations([dict(a, description="wrong") for a in self.observed], self.expected)

    def test_one_sample_shift_fails(self):
        with self.assertRaisesRegex(AssertionError, "offset"):
            verify_annotations([dict(a, onset=4.002) for a in self.observed], self.expected)


if __name__ == "__main__":
    unittest.main()
