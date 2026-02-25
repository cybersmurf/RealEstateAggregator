import httpx, asyncio, re, json

async def t():
    h = {"User-Agent": "Mozilla/5.0 Chrome/122.0", "Accept-Language": "cs-CZ"}
    async with httpx.AsyncClient(timeout=15, follow_redirects=True, headers=h) as c:
        r = await c.get("https://www.reas.cz")
        # Get __NEXT_DATA__ and look for nav/categories
        match = re.search(r'<script id="__NEXT_DATA__"[^>]*>(.*?)</script>', r.text, re.DOTALL)
        if match:
            data = json.loads(match.group(1))
            # Print all top-level pageProps keys
            pages = data.get("props", {}).get("pageProps", {})
            print("pageProps keys on homepage:", list(pages.keys())[:10])
        
        # Find nav links
        hrefs = re.findall(r'href="(/[^"]+)"', r.text)
        unique = sorted(set(h for h in hrefs if len(h) < 40))
        print("\nAll short internal links:")
        for link in unique[:30]:
            print(f"  {link}")

asyncio.run(t())
