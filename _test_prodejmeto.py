import asyncio, sys, re
sys.path.insert(0, 'scraper')
import httpx
from bs4 import BeautifulSoup

BASE_URL = "https://www.prodejme.to"
AJAX_URL = f"{BASE_URL}/nabidky/ajax/"
DEFAULT_HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/122.0.0.0 Safari/537.36",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    "Referer": f"{BASE_URL}/nabidky/",
    "X-Requested-With": "XMLHttpRequest",
}

async def test():
    async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
        # 1) Test AJAX page 1 to see how many total listings
        print("Testing AJAX page 1...")
        resp = await client.post(AJAX_URL, data={"page": 1, "sold": "0"},
                                 headers={"Content-Type": "application/x-www-form-urlencoded"})
        print(f"  Status: {resp.status_code}")
        data = resp.json()
        total = data.get("count", 0)
        print(f"  Total listings (no sold): {total}")
        
        # List all slugs from first page
        soup = BeautifulSoup(data.get("html", ""), "html.parser")
        slugs_p1 = []
        for card in soup.find_all("div", class_="project-item"):
            link = card.select_one("h3.title a, h2.title a")
            if link:
                href = link.get("href", "")
                slug = href.rstrip("/").split("/")[-1]
                slugs_p1.append(slug)
        print(f"  Page 1 slugs: {slugs_p1}")

        # 2) Try fetching detail for suchohrdly directly
        print("\nTesting Suchohrdly detail page...")
        detail = await client.get(f"{BASE_URL}/nabidky/suchohrdly")
        print(f"  Status: {detail.status_code}, Final URL: {detail.url}")
        if detail.status_code == 200:
            dsoup = BeautifulSoup(detail.text, "html.parser")
            h1 = dsoup.find("h1") or dsoup.find("h2")
            print(f"  Title: {h1.get_text(strip=True) if h1 else 'NOT FOUND'}")

        # 3) Check all pages for suchohrdly slug
        import math
        pages = max(1, math.ceil(total / 9))
        print(f"\nSearching ALL {pages} pages for 'suchohrdly'...")
        found = False
        for page in range(1, pages + 1):
            r = await client.post(AJAX_URL, data={"page": page, "sold": "0"},
                                  headers={"Content-Type": "application/x-www-form-urlencoded"})
            s = BeautifulSoup(r.json().get("html", ""), "html.parser")
            for card in s.find_all("div", class_="project-item"):
                lnk = card.select_one("h3.title a, h2.title a")
                if lnk:
                    href = lnk.get("href", "")
                    slug = href.rstrip("/").split("/")[-1]
                    title = lnk.get_text(strip=True)
                    if "suchohrdl" in slug.lower() or "suchohrdl" in title.lower():
                        print(f"  FOUND on page {page}: slug={slug}, title={title}")
                        found = True
            await asyncio.sleep(0.2)
        if not found:
            print("  NOT FOUND in any AJAX page")

asyncio.run(test())
