# SPDX-License-Identifier: Apache-2.0
"""Regression tests for remembered head movement beyond monitor boundaries."""

import unittest

from screen_edges import EdgeMemory, Monitor, project_to_monitors


class TestEdgeMemory(unittest.TestCase):
    def setUp(self):
        self.monitors = [
            Monitor(0, 0, 3840, 2160),
            Monitor(3840, 0, 1920, 1080),
            Monitor(5760, 0, 3840, 2160),
        ]
        self.memory = EdgeMemory()

    def move(self, dx, dy, actual=None):
        if actual is None:
            actual = self.memory.visible
        return self.memory.move(dx, dy, actual, self.monitors)

    def test_bottom_remains_pinned_until_extra_movement_is_undone(self):
        self.assertEqual(self.move(0, 200, (4800, 1000)), (4800, 1079))
        self.assertEqual(self.move(0, 150), (4800, 1079))
        self.assertEqual(self.move(0, -200), (4800, 1079))
        self.assertEqual(self.move(0, -100), (4800, 1050))

    def test_short_monitor_does_not_jump_to_a_taller_neighbor(self):
        self.assertEqual(self.move(0, 10000, (4800, 1000)), (4800, 1079))
        self.assertEqual(self.move(0, -5000), (4800, 1079))

    def test_overflow_does_not_block_movement_along_the_other_axis(self):
        self.move(0, 200, (4800, 1000))
        self.assertEqual(self.move(100, 0), (4900, 1079))

    def test_crossing_to_taller_monitor_retains_virtual_height(self):
        self.move(0, 500, (5500, 1000))
        self.assertEqual(self.move(300, 0), (5800, 1500))

    def test_reversing_all_movement_returns_to_the_original_position(self):
        self.move(100, 500, (4800, 1000))
        self.assertEqual(self.move(-100, -500), (4800, 1000))

    def test_all_four_edges_retain_overshoot(self):
        for dx, dy in [(20000, 0), (-20000, 0), (0, 20000), (0, -20000)]:
            with self.subTest(dx=dx, dy=dy):
                self.memory.reset()
                edge = self.move(dx, dy, (2000, 1000))
                self.assertEqual(self.move(-dx * 0.1, -dy * 0.1), edge)
                self.assertEqual(self.move(-dx * 0.9, -dy * 0.9), (2000, 1000))

    def test_physical_mouse_movement_establishes_a_new_anchor(self):
        self.move(0, 1000, (4800, 1000))
        self.assertEqual(self.move(0, -10, (2000, 1000)), (2000, 990))

    def test_recenter_discards_old_overflow(self):
        self.move(0, 1000, (4800, 1000))
        self.assertGreater(self.memory.overflow[1], 0)
        self.memory.reset()
        self.assertEqual(self.memory.overflow, (0.0, 0.0))
        self.assertEqual(self.move(0, -10, (1920, 1080)), (1920, 1070))

    def test_fractional_movement_accumulates(self):
        self.move(0.2, 0, (2000, 1000))
        for _ in range(4):
            position = self.move(0.2, 0)
        self.assertEqual(position, (2001, 1000))

    def test_vertically_stacked_and_negative_offset_monitors(self):
        monitors = [Monitor(-1920, 0, 1920, 1080), Monitor(0, -1080, 1920, 1080)]
        self.assertEqual(project_to_monitors(-100, 1500, monitors), (-100, 1079))
        self.assertEqual(project_to_monitors(100, 1500, monitors), (100, -1))


if __name__ == "__main__":
    unittest.main()
