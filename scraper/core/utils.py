"""
Utility funkce pro profiling, timing a monitoring scraperů.
"""
import time
import logging
from typing import Callable, Any
from functools import wraps
from contextlib import contextmanager

logger = logging.getLogger(__name__)


@contextmanager
def timer(name: str, log_level: int = logging.INFO):
    """
    Context manager pro měření času operace.
    
    Usage:
        with timer("Fetch page"):
            html = await fetch_page(url)
    """
    start = time.perf_counter()
    try:
        yield
    finally:
        elapsed = time.perf_counter() - start
        logger.log(log_level, f"{name} took {elapsed:.2f}s")


def timed(func: Callable) -> Callable:
    """
    Dekorátor pro měření času async funkce.
    
    Usage:
        @timed
        async def scrape_listing(url):
            ...
    """
    @wraps(func)
    async def wrapper(*args, **kwargs) -> Any:
        start = time.perf_counter()
        try:
            result = await func(*args, **kwargs)
            return result
        finally:
            elapsed = time.perf_counter() - start
            logger.info(f"{func.__name__} took {elapsed:.2f}s")
            
    return wrapper


class ScraperMetrics:
    """
    Simple metrics collector pro scraper performance.
    """
    
    def __init__(self):
        self.reset()
        
    def reset(self):
        """Reset všech metrik."""
        self.pages_scraped = 0
        self.pages_failed = 0
        self.total_time = 0.0
        self.fetch_times = []
        self.parse_times = []
        self.save_times = []
        
    def record_fetch(self, duration: float):
        """Zaznamenej čas fetchování stránky."""
        self.fetch_times.append(duration)
        
    def record_parse(self, duration: float):
        """Zaznamenej čas parsování HTML."""
        self.parse_times.append(duration)
        
    def record_save(self, duration: float):
        """Zaznamenej čas ukládání do DB."""
        self.save_times.append(duration)
        
    def increment_scraped(self):
        """Increment počítadlo scrapnutých stránek."""
        self.pages_scraped += 1
        
    def increment_failed(self):
        """Increment počítadlo failed stránek."""
        self.pages_failed += 1
        
    def summary(self) -> dict:
        """Vrátí summary metrik."""
        def avg(times):
            return sum(times) / len(times) if times else 0.0
            
        return {
            "pages_scraped": self.pages_scraped,
            "pages_failed": self.pages_failed,
            "success_rate": self.pages_scraped / (self.pages_scraped + self.pages_failed) if (self.pages_scraped + self.pages_failed) > 0 else 0.0,
            "avg_fetch_time": avg(self.fetch_times),
            "avg_parse_time": avg(self.parse_times),
            "avg_save_time": avg(self.save_times),
            "total_time": self.total_time,
        }
        
    def log_summary(self):
        """Logni summary metrik."""
        s = self.summary()
        logger.info(
            f"Scraper metrics: "
            f"{s['pages_scraped']} scraped, "
            f"{s['pages_failed']} failed, "
            f"success rate: {s['success_rate']:.1%}, "
            f"avg fetch: {s['avg_fetch_time']:.2f}s, "
            f"avg parse: {s['avg_parse_time']:.2f}s, "
            f"avg save: {s['avg_save_time']:.2f}s, "
            f"total: {s['total_time']:.2f}s"
        )


@contextmanager
def scraper_metrics_context():
    """
    Context manager pro automatické logování metrik.
    
    Usage:
        with scraper_metrics_context() as metrics:
            # scraping logic
            metrics.increment_scraped()
    """
    metrics = ScraperMetrics()
    start = time.perf_counter()
    
    try:
        yield metrics
    finally:
        metrics.total_time = time.perf_counter() - start
        metrics.log_summary()
