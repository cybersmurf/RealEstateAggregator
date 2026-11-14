"""REALmix za ochranou Anubis (14. 11. 2026): přiznaný klient, výzva není prázdný výpis."""
import httpx
import pytest

from core.scrapers.realmix_scraper import DEFAULT_HEADERS, RealmixScraper


def test_realmix_identifies_honestly_behind_anubis() -> None:
    # Anubis posílá výzvu každému, kdo se hlásí jako „Mozilla/…“
    assert "Mozilla" not in DEFAULT_HEADERS["User-Agent"]
    assert DEFAULT_HEADERS["User-Agent"].startswith("RealEstateAggregator/")


@pytest.mark.asyncio
async def test_realmix_challenge_page_is_an_error_not_an_empty_list() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.startswith("/.within.website/"):
            return httpx.Response(200, text="<title>Making sure you're not a bot!</title>")
        return httpx.Response(302, headers={
            "Location": f"https://www.reality-znojmo.cz/.within.website/?redir={request.url}"})

    scraper = RealmixScraper()
    async with httpx.AsyncClient(transport=httpx.MockTransport(handler), follow_redirects=True) as client:
        scraper._http_client = client
        with pytest.raises(RuntimeError):
            await scraper._fetch.__wrapped__(scraper, "https://www.reality-znojmo.cz/prodej/rodinne-domy/")


def test_audit_live_check_uses_honest_agent_for_realmix() -> None:
    from scripts.quality_audit import _headers_for

    assert "Mozilla" not in _headers_for("https://www.reality-znojmo.cz/prodej/x-1234567.html")["User-Agent"]
    assert _headers_for("https://www.sreality.cz/detail/x/1") == {}
