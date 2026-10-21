from app.cpu import thread_budget


def test_thread_budget_is_eighty_percent_rounded_down():
    assert thread_budget(0.8, 16) == 12
    assert thread_budget(0.8, 4) == 3


def test_thread_budget_stays_within_one_and_core_count():
    assert thread_budget(0.01, 8) == 1
    assert thread_budget(1.5, 8) == 8
