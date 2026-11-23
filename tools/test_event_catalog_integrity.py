import unittest

from check_event_catalog_integrity import known_mapping_correction


class ReviewedCorrectionTests(unittest.TestCase):
    def test_gta_requires_exact_recipe_preserving_renumbering(self):
        old = {"49": {"event": "base"}, "51": {"event": "perspective"},
               "59": {"event": "heist"}, "80": {"event": "masterminds"}}
        corrected = {"49": old["49"], "50": old["51"],
                     "58": old["59"], "79": old["80"]}
        self.assertTrue(known_mapping_correction("1050025307", old, corrected))
        self.assertFalse(known_mapping_correction("1050025307", old, {"49": old["49"]}))
        self.assertFalse(known_mapping_correction("1050025307", old,
                         {**corrected, "58": {"event": "unreviewed"}}))

    def test_eso_requires_exact_move_with_no_other_removals(self):
        old = {"106": {"event": "quest"}, "107": {"event": "sweepers"},
               "109": {"event": "wrong"}}
        corrected = {"106": old["106"], "109": old["107"]}
        self.assertTrue(known_mapping_correction("1285166951", old, corrected))
        self.assertFalse(known_mapping_correction("1285166951", old, {"109": old["107"]}))
        self.assertFalse(known_mapping_correction("1285166951", old,
                         {**corrected, "109": {"event": "guessed"}}))
        self.assertFalse(known_mapping_correction("999", old, corrected))


if __name__ == "__main__":
    unittest.main()
