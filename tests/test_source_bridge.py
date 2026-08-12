import time
import unittest

from tools.source_bridge import (
    LatestFrameReader,
    TimelineDriftGuard,
    timeline_drift_seconds,
)


class SlowFrameStream:
    def __init__(self, frames, delay=0.002):
        self.frames = list(frames)
        self.delay = delay
        self.index = 0

    def read(self, size):
        if self.index >= len(self.frames):
            return b""
        time.sleep(self.delay)
        frame = self.frames[self.index]
        self.index += 1
        return frame


class LatestFrameReaderTests(unittest.TestCase):
    def test_reader_discards_intermediate_frames(self):
        reader = LatestFrameReader(
            SlowFrameStream([b"aaaa", b"bbbb", b"cccc"]), 4)
        reader.start()
        time.sleep(0.02)

        latest = reader.latest_after(0)

        self.assertEqual(latest.sequence, 3)
        self.assertEqual(latest.data, b"cccc")
        reader.stop()
        reader.join()

    def test_reader_returns_each_frame_when_consumer_keeps_up(self):
        reader = LatestFrameReader(
            SlowFrameStream([b"aaaa", b"bbbb"], delay=0.02), 4)
        reader.start()

        first = reader.latest_after(0)
        second = reader.latest_after(first.sequence)

        self.assertEqual(first.data, b"aaaa")
        self.assertEqual(second.data, b"bbbb")
        reader.stop()
        reader.join()


class TimelineDriftTests(unittest.TestCase):
    def test_drift_uses_decoded_sequence(self):
        self.assertAlmostEqual(
            timeline_drift_seconds(100.25, 101.25, 25, 24), 0.0)

    def test_guard_requires_sustained_drift(self):
        guard = TimelineDriftGuard(0.75, 2)

        self.assertFalse(guard.observe(0.80))
        self.assertTrue(guard.observe(0.90))

    def test_guard_resets_after_good_sample(self):
        guard = TimelineDriftGuard(0.75, 2)

        self.assertFalse(guard.observe(0.80))
        self.assertFalse(guard.observe(0.10))
        self.assertFalse(guard.observe(0.80))

    def test_guard_restarts_immediately_after_seek(self):
        guard = TimelineDriftGuard(0.75, 3, hard_threshold=5.0)

        self.assertTrue(guard.observe(30.0))


if __name__ == "__main__":
    unittest.main()
