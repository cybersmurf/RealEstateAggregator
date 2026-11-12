"""Job přerušený restartem kontejneru se po startu spustí znovu (api/main.py)."""
from api.main import pick_job_to_resume


def _job(job_id: str, full: bool, age: float) -> dict:
    return {"id": job_id, "source_codes": ["SREALITY", "BAZOS"], "full_rescan": full, "age_hours": age}


def test_nightly_job_killed_by_reboot_is_resumed() -> None:
    # 12. 11. 2026: restart serveru 2 s po začátku nočního běhu, kontejner zpět za 74 s
    job = _job("night", True, 0.02)
    assert pick_job_to_resume([job], 0) is job


def test_old_leftover_job_is_not_resumed() -> None:
    assert pick_job_to_resume([_job("old", True, 30.0)], 0) is None
    assert pick_job_to_resume([], 0) is None


def test_repeated_restart_does_not_loop() -> None:
    assert pick_job_to_resume([_job("night", True, 0.5)], 1) is None


def test_full_rescan_wins_over_newer_incremental() -> None:
    full, incremental = _job("full", True, 2.0), _job("inc", False, 0.1)
    assert pick_job_to_resume([incremental, full], 0) is full
