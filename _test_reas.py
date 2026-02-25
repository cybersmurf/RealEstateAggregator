import asyncio, sys, re, json
sys.path.insert(0, 'scraper')
import httpx

BASE_URL = "https://www.reas.cz"
DEFAULT_HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/122.0.0.0 Safari/537.36",
    "Accept-Language": "cs-CZ,cs;q=0.9",
    "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
}

async def test():
    async with httpx.AsyncClient(timeout=30, follow_redirects=True, headers=DEFAULT_HEADERS) as client:
        for category in ["prodej/byty", "prodej/domy"]:
            url = f"{BASE_URL}/{category}?page=1"
            print(f"\nFetching: {url}")
            resp = await client.get(url)
            print(f"  Status: {resp.status_code}, Final URL: {resp.url}")
            match = re.search(r'<script id="__NEXT_DATA__"[^>]*>(.*?)</script>', resp.text, re.DOTALL)
            if match:
                try:
                    data = json.loads(match.group(1))
                    props = data.get("props", {}).get("pageProps", {})
                    print(f"  pageProps keys: {list(props.keys())}")
                    ads = props.get("adsListResult")
                    if ads is None:
                        # Try alternative keys
                        for k, v in props.items():
                            print(f"    {k}: {type(v).__name__} = {str(v)[:100]}")
                    else:
                        print(f"  adsListResult type: {type(ads).__name__}")
                        if isinstance(ads, dict):
                            print(f"  count={ads.get('count')}, data items={len(ads.get('data', []))}")
                            if ads.get('data'):
                                print(f"  first item keys: {list(ads['data'][0].keys())}")
                        else:
                            print(f"  adsListResult value: {str(ads)[:200]}")
                except Exception as e:
                    print(f"  Parse error: {e}")
            else:
                print("  NO __NEXT_DATA__ found!")
                print(f"  Body preview: {resp.text[:500]}")

asyncio.run(test())
