"""Propad výsledků zdroje proti minulým běhům – rozbitý parser vrací zlomek, ne nulu."""
from core.notifications import detect_drops


class TestDetectDrops:
    def test_propad_pod_60_procent_medianu(self):
        assert detect_drops({"IDNES": 120}, {"IDNES": [2900, 2850, 2910]}) == {"IDNES": (120, 2900)}

    def test_bezne_kolisani_se_nehlasi(self):
        assert detect_drops({"SREALITY": 540}, {"SREALITY": [600, 590, 610]}) == {}

    def test_maly_zdroj_se_nehlasi(self):
        assert detect_drops({"LEXAMO": 3}, {"LEXAMO": [14, 15, 13]}) == {}

    def test_bez_historie_nebo_jediny_beh_se_nehlasi(self):
        assert detect_drops({"NOVY": 1, "REAS": 10}, {"REAS": [300]}) == {}

    def test_median_odola_jednomu_vybocujicimu_behu(self):
        assert detect_drops({"BAZOS": 700}, {"BAZOS": [1200, 10, 1250, 1230]}) == {"BAZOS": (700, 1215)}
