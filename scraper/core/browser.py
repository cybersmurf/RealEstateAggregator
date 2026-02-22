"""
Optimalizovaný Playwright browser manager pro rychlé scrapování.

Features:
- Single browser instance s reuse
- Multiple contexts pro paralelizaci
- Resource blocking (images, fonts, media)
- Smart waiting místo sleep
- Semaphore pro limit paralelismu
"""
import asyncio
import logging
from typing import Optional, Callable, Any
from contextlib import asynccontextmanager

from playwright.async_api import async_playwright, Browser, BrowserContext, Page

logger = logging.getLogger(__name__)


class PlaywrightBrowserManager:
    """
    Manager pro Playwright browser s optimalizacemi pro rychlé scrapování.
    """
    
    def __init__(
        self,
        headless: bool = True,
        max_concurrent_contexts: int = 8,
        block_resources: bool = True,
        timeout_ms: int = 30000,
    ):
        self.headless = headless
        self.max_concurrent_contexts = max_concurrent_contexts
        self.block_resources = block_resources
        self.timeout_ms = timeout_ms
        
        self._playwright = None
        self._browser: Optional[Browser] = None
        self._semaphore = asyncio.Semaphore(max_concurrent_contexts)
        
    async def start(self) -> None:
        """Spustí Playwright a browser."""
        if self._browser is not None:
            logger.warning("Browser already started")
            return
            
        logger.info(f"Starting Playwright browser (headless={self.headless})")
        self._playwright = await async_playwright().start()
        
        self._browser = await self._playwright.chromium.launch(
            headless=self.headless,
            args=[
                "--disable-dev-shm-usage",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-gpu",
                "--disable-notifications",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
            ]
        )
        logger.info("Browser started successfully")
        
    async def close(self) -> None:
        """Zavře browser a Playwright."""
        if self._browser:
            await self._browser.close()
            self._browser = None
            
        if self._playwright:
            await self._playwright.stop()
            self._playwright = None
            
        logger.info("Browser closed")
        
    @asynccontextmanager
    async def get_context(self):
        """
        Context manager pro získání browser contextu.
        
        Usage:
            async with manager.get_context() as (context, page):
                await page.goto(url)
                html = await page.content()
        """
        if self._browser is None:
            raise RuntimeError("Browser not started. Call start() first.")
            
        # Semaphore limit paralelismu
        async with self._semaphore:
            context = await self._browser.new_context()
            
            # Apply resource blocking
            if self.block_resources:
                await self._setup_resource_blocking(context)
                
            page = await context.new_page()
            page.set_default_timeout(self.timeout_ms)
            
            try:
                yield context, page
            finally:
                await context.close()
                
    async def _setup_resource_blocking(self, context: BrowserContext) -> None:
        """
        Nastaví route interceptor pro blokování zbytečných resources.
        Blokuje: images, fonts, media, stylesheets
        """
        async def route_intercept(route, request):
            resource_type = request.resource_type
            
            # Blokuj zbytečné typy
            if resource_type in ("image", "font", "media", "stylesheet"):
                await route.abort()
            else:
                await route.continue_()
                
        await context.route("**/*", route_intercept)
        logger.debug("Resource blocking enabled for context")
        
    async def fetch_page(
        self,
        url: str,
        wait_for_selector: Optional[str] = None,
        wait_for_state: str = "domcontentloaded",
        scroll_to_bottom: bool = False,
    ) -> str:
        """
        Načte stránku pomocí Playwright a vrátí HTML.
        
        Args:
            url: URL stránky
            wait_for_selector: CSS selector, na který se má čekat (optional)
            wait_for_state: Load state - "load", "domcontentloaded", "networkidle"
            scroll_to_bottom: Zda scrollovat dolů (pro infinite scroll)
            
        Returns:
            HTML content stránky
        """
        async with self.get_context() as (context, page):
            logger.debug(f"Fetching {url}")
            
            await page.goto(url, wait_until=wait_for_state, timeout=self.timeout_ms)
            
            # Počkej na specifický selector
            if wait_for_selector:
                await page.wait_for_selector(wait_for_selector, timeout=10000)
                
            # Scroll down pro infinite scroll
            if scroll_to_bottom:
                await self._scroll_to_bottom(page)
                
            html = await page.content()
            logger.debug(f"Fetched {len(html)} bytes from {url}")
            return html
            
    async def _scroll_to_bottom(self, page: Page) -> None:
        """
        Scrolluje stránku dolů dokud se nenačte všechen obsah.
        Používá se pro infinite scroll stránky.
        """
        logger.debug("Scrolling to bottom...")
        previous_height = None
        attempts = 0
        max_attempts = 10
        
        while attempts < max_attempts:
            current_height = await page.evaluate("document.body.scrollHeight")
            
            if previous_height == current_height:
                break
                
            previous_height = current_height
            await page.mouse.wheel(0, 5000)
            await page.wait_for_timeout(500)
            attempts += 1
            
        logger.debug(f"Scrolled to bottom in {attempts} attempts")
        
    async def fetch_many(
        self,
        urls: list[str],
        fetch_func: Callable[[str], Any],
        return_exceptions: bool = True,
    ) -> list[Any]:
        """
        Paralelní fetch více URL pomocí asyncio.gather.
        
        Args:
            urls: Seznam URL k načtení
            fetch_func: Async funkce která zpracuje jednu URL
            return_exceptions: Zda vrátit exceptions místo crashe
            
        Returns:
            List výsledků pro každou URL
        """
        tasks = [fetch_func(url) for url in urls]
        results = await asyncio.gather(*tasks, return_exceptions=return_exceptions)
        return results


# Singleton instance pro reuse across scrapers
_browser_manager: Optional[PlaywrightBrowserManager] = None


async def get_browser_manager(
    headless: bool = True,
    max_concurrent: int = 8,
    block_resources: bool = True,
) -> PlaywrightBrowserManager:
    """
    Získá singleton instance browser manageru.
    Automaticky startuje browser pokud ještě není spuštěný.
    """
    global _browser_manager
    
    if _browser_manager is None:
        _browser_manager = PlaywrightBrowserManager(
            headless=headless,
            max_concurrent_contexts=max_concurrent,
            block_resources=block_resources,
        )
        await _browser_manager.start()
        
    return _browser_manager


async def close_browser_manager() -> None:
    """Zavře globální browser manager."""
    global _browser_manager
    
    if _browser_manager is not None:
        await _browser_manager.close()
        _browser_manager = None
