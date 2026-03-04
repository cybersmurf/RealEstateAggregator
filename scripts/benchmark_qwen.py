#!/usr/bin/env python3
"""
Benchmark: Qwen2.5:14b vs Qwen3.5:9b
Porovnání rychlosti a kvality odpovědí na reálných úlohách z RealEstateAggregator.
"""
import asyncio
import httpx
import json
import time
from datetime import datetime
from typing import Dict, List, Any
from statistics import mean, stdev

OLLAMA_BASE_URL = "http://localhost:11434"

# Test úlohy - reálné příklady z projektu
TEST_CASES = [
    {
        "name": "Normalizace popisu",
        "prompt": """Normalizuj tento popis nemovitosti - oprav překlepy, gramatiku, zachovej všechny fakta:

Prodej rodinného domu v obci Kuchařovice, okres Znojmo. Dispozice 5+1, celková plocha 180 m2. 
Nemovitost je ve velmi dobrém stavu, provedena kompletni rekonstrukce v roce 2015. 
V objektu je kk půda s možností vestavby podkroví. Pozemek 850m2, zahrada, garáž.
Cena: 4.390.000 Kč""",
        "expected_keywords": ["rodinný dům", "Kuchařovice", "5+1", "180 m²", "rekonstrukce", "2015", "850 m²"]
    },
    {
        "name": "Smart Tags - extrakce",
        "prompt": """Z tohoto popisu extrahuj smart tags (jen klíčové vlastnosti, 1-3 slova, max 8 tagů):

Prodej bytu 3+1 v centru Brna, ulice Nádraží. Dispozice 75 m², 2. patro s výtahem.
Kompletní rekonstrukce 2020 - nová kuchyňská linka, plastová okna, renovovaná koupelna.
Nízké měsíční náklady 3.500 Kč. Výborná dostupnost MHD, školy v dosahu.
Sklep 4 m². Cena 6.200.000 Kč.""",
        "expected_keywords": ["centrum", "výtah", "rekonstrukce", "MHD", "sklep"]
    },
    {
        "name": "Q&A - odpověď na otázku",
        "prompt": """Na základě tohoto inzerátu odpověz na otázku: "Je tam garáž?"

Prodej RD 4+1 v Praze 6, Dejvice. Dispozice 140 m², pozemek 320 m².
Dům po rekonstrukci, nová střecha 2018, plastová okna.
Zahrada, terasa 25 m². Parkování na pozemku (2 auta).
Cena: 12.500.000 Kč

Odpověď (ano/ne + zdůvodnění):""",
        "expected_answer": "ne"  # parkování != garáž
    },
    {
        "name": "Cenová analýza",
        "prompt": """Analyzuj tuto cenu a řekni jestli je přiměřená (jen 1-2 věty):

Byt 2+kk, Praha 10, 45 m², 3. patro bez výtahu, panel po rekonstrukci (2019).
Cena: 5.800.000 Kč (129.000 Kč/m²)

Kontext: Průměr pro Praha 10 panelové byty je ~110.000 Kč/m².""",
        "expected_keywords": ["dražší", "nad průměr"]
    },
    {
        "name": "Krátká odpověď",
        "prompt": "Kolik je pater v panelovém domě kategorie P1?",
        "expected_answer": "4"
    }
]

class ModelBenchmark:
    def __init__(self, model_name: str):
        self.model_name = model_name
        self.client = httpx.AsyncClient(timeout=120.0)
        self.results: List[Dict[str, Any]] = []
    
    async def warmup(self) -> None:
        """Zahřeje model (načte do RAMu) před samotným měřením."""
        print(f"  Warming up {self.model_name}...", end=" ", flush=True)
        try:
            await self.client.post(
                f"{OLLAMA_BASE_URL}/api/chat",
                json={
                    "model": self.model_name,
                    "messages": [{"role": "user", "content": "Ahoj"}],
                    "stream": False,
                    "options": {"num_predict": 10, "temperature": 0.0}
                },
                timeout=120.0
            )
            print("✓")
        except Exception as e:
            print(f"✗ {e}")

    async def generate(self, prompt: str) -> Dict[str, Any]:
        """Generuj odpověď a měř čas (přes /api/chat – funguje pro všechny modely včetně Qwen3)."""
        start = time.perf_counter()
        
        try:
            response = await self.client.post(
                f"{OLLAMA_BASE_URL}/api/chat",
                json={
                    "model": self.model_name,
                    "messages": [{"role": "user", "content": prompt}],
                    "stream": False,
                    "options": {
                        "temperature": 0.1,  # konzistentní výstupy
                        "num_predict": 512
                    }
                },
                timeout=300.0
            )
            response.raise_for_status()
            data = response.json()
            
            elapsed = time.perf_counter() - start
            
            # /api/chat response structure
            content = data.get("message", {}).get("content", "").strip()
            eval_count = data.get("eval_count", 0)
            
            return {
                "success": True,
                "response": content,
                "elapsed_seconds": elapsed,
                "eval_count": eval_count,
                "tokens_per_second": eval_count / elapsed if elapsed > 0 else 0
            }
        except Exception as e:
            return {
                "success": False,
                "error": str(e),
                "elapsed_seconds": time.perf_counter() - start
            }
    
    async def run_test_case(self, test_case: Dict[str, Any], iterations: int = 3) -> Dict[str, Any]:
        """Spusť jeden test case N-krát pro měření konzistence."""
        print(f"  [{self.model_name}] Test: {test_case['name']}")
        
        results = []
        for i in range(iterations):
            print(f"    Iteration {i+1}/{iterations}...", end=" ", flush=True)
            result = await self.generate(test_case["prompt"])
            results.append(result)
            
            if result["success"]:
                print(f"✓ {result['elapsed_seconds']:.2f}s ({result['tokens_per_second']:.1f} tok/s)")
            else:
                print(f"✗ {result.get('error', 'Unknown error')}")
        
        # Agregace výsledků
        successful = [r for r in results if r["success"]]
        if not successful:
            return {
                "test_name": test_case["name"],
                "success_rate": 0,
                "results": results
            }
        
        times = [r["elapsed_seconds"] for r in successful]
        tokens_per_sec = [r["tokens_per_second"] for r in successful]
        
        return {
            "test_name": test_case["name"],
            "success_rate": len(successful) / len(results),
            "avg_time": mean(times),
            "std_time": stdev(times) if len(times) > 1 else 0,
            "avg_tokens_per_sec": mean(tokens_per_sec),
            "responses": [r["response"] for r in successful],
            "results": results
        }
    
    async def run_all_tests(self) -> List[Dict[str, Any]]:
        """Spusť všechny test cases."""
        print(f"\n{'='*60}")
        print(f"Benchmarking model: {self.model_name}")
        print(f"{'='*60}\n")
        
        await self.warmup()  # načíst model do paměti před měřením
        await asyncio.sleep(2)
        
        results = []
        for test_case in TEST_CASES:
            result = await self.run_test_case(test_case, iterations=3)
            results.append(result)
            await asyncio.sleep(1)  # pauza mezi testy
        
        self.results = results
        return results
    
    async def close(self):
        await self.client.aclose()


async def compare_models():
    """Porovnej oba modely."""
    print(f"\n{'#'*60}")
    print(f"# Benchmark: qwen2.5:14b vs qwen3.5:9b - {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"{'#'*60}")
    
    # Benchmark obou modelů
    qwen25_14b = ModelBenchmark("qwen2.5:14b")
    qwen35_9b = ModelBenchmark("qwen3.5:9b")
    
    results_14b = await qwen25_14b.run_all_tests()
    results_9b = await qwen35_9b.run_all_tests()
    
    # Výsledky
    print(f"\n{'='*60}")
    print("VÝSLEDKY POROVNÁNÍ")
    print(f"{'='*60}\n")
    
    # Tabulka rychlosti
    print(f"{'Test':<30} {'14B (s)':<12} {'9B (s)':<12} {'Rozdíl':<10}")
    print("-" * 64)
    
    total_time_14b = 0
    total_time_9b = 0
    total_tokens_14b = 0
    total_tokens_9b = 0
    
    for r14, r9 in zip(results_14b, results_9b):
        name = r14["test_name"][:28]
        
        if r14["success_rate"] > 0 and r9["success_rate"] > 0:
            time_14 = r14["avg_time"]
            time_9 = r9["avg_time"]
            diff_pct = ((time_9 - time_14) / time_14) * 100
            
            total_time_14b += time_14
            total_time_9b += time_9
            total_tokens_14b += r14["avg_tokens_per_sec"]
            total_tokens_9b += r9["avg_tokens_per_sec"]
            
            faster = "3.5:9B rychlejší" if diff_pct < 0 else "2.5:14B rychlejší"
            print(f"{name:<30} {time_14:>10.2f}  {time_9:>10.2f}  {diff_pct:>+8.1f}%  {faster}")
        else:
            print(f"{name:<30} {'FAILED':<12} {'FAILED':<12}")
    
    print("-" * 64)
    if total_time_14b > 0 and total_time_9b > 0:
        diff_total = (total_time_9b - total_time_14b) / total_time_14b * 100
        print(f"{'CELKEM':<30} {total_time_14b:>10.2f}  {total_time_9b:>10.2f}  {diff_total:>+8.1f}%")
    else:
        print(f"{'CELKEM':<30} {'N/A':<12} {'N/A':<12} (některý model selhal)")
    
    # Průměrná rychlost tokenů
    avg_tokens_14 = total_tokens_14b / len(TEST_CASES) if total_tokens_14b > 0 else 0
    avg_tokens_9 = total_tokens_9b / len(TEST_CASES) if total_tokens_9b > 0 else 0
    
    print(f"\nPrůměrná rychlost:")
    print(f"  Qwen2.5:14B → {avg_tokens_14:.1f} tokens/sec")
    if avg_tokens_14 > 0:
        print(f"  Qwen3.5:9B  → {avg_tokens_9:.1f} tokens/sec ({((avg_tokens_9 - avg_tokens_14) / avg_tokens_14 * 100):+.1f}%)")
    else:
        print(f"  Qwen3.5:9B  → {avg_tokens_9:.1f} tokens/sec")
    
    # Porovnání kvality odpovědí
    print(f"\n{'='*60}")
    print("KVALITA ODPOVĚDÍ (ukázky)")
    print(f"{'='*60}\n")
    
    for i, (r14, r9) in enumerate(zip(results_14b, results_9b)):
        if r14["success_rate"] > 0 and r9["success_rate"] > 0:
            print(f"\n{i+1}. {r14['test_name']}")
            print("-" * 60)
            print(f"Qwen2.5:14B:")
            print(f"  {r14['responses'][0][:200]}{'...' if len(r14['responses'][0]) > 200 else ''}")
            print(f"\nQwen3.5:9B:")
            print(f"  {r9['responses'][0][:200]}{'...' if len(r9['responses'][0]) > 200 else ''}")
    
    # Uložení detailních výsledků
    output_file = f"benchmark_qwen_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
    with open(output_file, "w", encoding="utf-8") as f:
        json.dump({
            "timestamp": datetime.now().isoformat(),
            "models": {
                "qwen2.5:14b": results_14b,
                "qwen3.5:9b": results_9b
            }
        }, f, indent=2, ensure_ascii=False)
    
    print(f"\n\nDetailní výsledky uloženy do: {output_file}")
    
    # Doporučení
    print(f"\n{'='*60}")
    print("DOPORUČENÍ")
    print(f"{'='*60}\n")
    
    speedup = ((total_time_14b - total_time_9b) / total_time_14b * 100) if total_time_14b > 0 else 0
    
    if speedup > 20:
        print(f"✅ Qwen3.5:9B je {speedup:.1f}% rychlejší → DOPORUČUJI přejít na 3.5:9B")
        print("   (pokud je kvalita odpovědí dostatečná)")
    elif speedup > 0:
        print(f"⚡ Qwen3.5:9B je {speedup:.1f}% rychlejší, ale rozdíl není velký")
        print("   → Zkontroluj kvalitu výstupů ručně před přechodem")
    else:
        print(f"⚠️  Qwen2.5:14B je rychlejší → ZŮSTAT u 2.5:14B")
    
    print("\nPorovnej kvalitu odpovědí výše a rozhodni se!")
    
    await qwen25_14b.close()
    await qwen35_9b.close()


if __name__ == "__main__":
    print("\n🔬 Qwen Model Benchmark: qwen2.5:14b vs qwen3.5:9b")
    print("\nSpouštím benchmark...\n")
    
    asyncio.run(compare_models())
