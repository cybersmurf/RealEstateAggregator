#!/usr/bin/env python3
"""
Benchmark: gemma4:12b-mlx vs gemma4:26b-mlx (Apple MLX via Ollama)
Porovnání rychlosti a kvality na reálných úlohách z RealEstateAggregator.

Spuštění:
  mcp/.venv/bin/python scripts/benchmark_gemma4.py
"""
import asyncio
import json
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any

import httpx

sys.path.insert(0, str(Path(__file__).parent))
from benchmark_qwen import ModelBenchmark, TEST_CASES, OLLAMA_BASE_URL  # noqa: E402

MODEL_SMALL = "gemma4:12b-mlx"
MODEL_LARGE = "gemma4:26b-mlx"


class GemmaBenchmark(ModelBenchmark):
    """Gemma 4 MLX – vypnout thinking režim pro přímé odpovědi."""

    async def generate(self, prompt: str) -> dict[str, Any]:
        start = time.perf_counter()
        try:
            response = await self.client.post(
                f"{OLLAMA_BASE_URL}/api/chat",
                json={
                    "model": self.model_name,
                    "messages": [
                        {
                            "role": "system",
                            "content": "Jsi asistent pro český realitní trh. Odpovídej přímo v češtině, stručně a věcně.",
                        },
                        {"role": "user", "content": prompt},
                    ],
                    "stream": False,
                    "think": False,
                    "options": {"temperature": 0.1, "num_predict": 1024},
                },
                timeout=600.0,
            )
            response.raise_for_status()
            data = response.json()
            elapsed = time.perf_counter() - start
            content = (data.get("message", {}).get("content") or "").strip()
            eval_count = data.get("eval_count", 0)
            return {
                "success": bool(content),
                "response": content,
                "elapsed_seconds": elapsed,
                "eval_count": eval_count,
                "tokens_per_second": eval_count / elapsed if elapsed > 0 else 0,
                "done_reason": data.get("done_reason"),
            }
        except Exception as e:
            return {
                "success": False,
                "error": str(e),
                "elapsed_seconds": time.perf_counter() - start,
            }


async def compare_models() -> None:
    print(f"\n{'#' * 60}")
    print(f"# Benchmark: {MODEL_SMALL} vs {MODEL_LARGE}")
    print(f"# {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"{'#' * 60}")

    small = GemmaBenchmark(MODEL_SMALL)
    large = GemmaBenchmark(MODEL_LARGE)

    results_small = await small.run_all_tests()
    results_large = await large.run_all_tests()

    print(f"\n{'=' * 60}")
    print("VÝSLEDKY POROVNÁNÍ")
    print(f"{'=' * 60}\n")

    print(f"{'Test':<30} {'12B (s)':<12} {'26B (s)':<12} {'Rozdíl':<10}")
    print("-" * 64)

    total_small = total_large = 0.0
    tokens_small = tokens_large = 0.0

    for rs, rl in zip(results_small, results_large):
        name = rs["test_name"][:28]
        if rs["success_rate"] > 0 and rl["success_rate"] > 0:
            t_s, t_l = rs["avg_time"], rl["avg_time"]
            diff_pct = ((t_l - t_s) / t_s) * 100 if t_s > 0 else 0
            total_small += t_s
            total_large += t_l
            tokens_small += rs["avg_tokens_per_sec"]
            tokens_large += rl["avg_tokens_per_sec"]
            faster = "12B rychlejší" if diff_pct > 0 else "26B rychlejší"
            print(f"{name:<30} {t_s:>10.2f}  {t_l:>10.2f}  {diff_pct:>+8.1f}%  {faster}")
        else:
            print(f"{name:<30} {'FAILED':<12} {'FAILED':<12}")

    print("-" * 64)
    if total_small > 0 and total_large > 0:
        diff_total = (total_large - total_small) / total_small * 100
        print(f"{'CELKEM':<30} {total_small:>10.2f}  {total_large:>10.2f}  {diff_total:>+8.1f}%")

    n = len(TEST_CASES)
    print(f"\nPrůměrná rychlost:")
    print(f"  {MODEL_SMALL} → {tokens_small / n:.1f} tokens/sec")
    print(f"  {MODEL_LARGE} → {tokens_large / n:.1f} tokens/sec")

    print(f"\n{'=' * 60}")
    print("KVALITA ODPOVĚDÍ (ukázky)")
    print(f"{'=' * 60}\n")

    for i, (rs, rl) in enumerate(zip(results_small, results_large)):
        if rs["success_rate"] > 0 and rl["success_rate"] > 0:
            print(f"\n{i + 1}. {rs['test_name']}")
            print("-" * 60)
            print(f"{MODEL_SMALL}:")
            print(f"  {rs['responses'][0][:300]}{'...' if len(rs['responses'][0]) > 300 else ''}")
            print(f"\n{MODEL_LARGE}:")
            print(f"  {rl['responses'][0][:300]}{'...' if len(rl['responses'][0]) > 300 else ''}")

    output_file = Path(__file__).parent.parent / f"benchmark_gemma4_{datetime.now().strftime('%Y%m%d_%H%M%S')}.json"
    with output_file.open("w", encoding="utf-8") as f:
        json.dump(
            {
                "timestamp": datetime.now().isoformat(),
                "models": {
                    MODEL_SMALL: results_small,
                    MODEL_LARGE: results_large,
                },
            },
            f,
            indent=2,
            ensure_ascii=False,
        )

    print(f"\n\nDetailní výsledky uloženy do: {output_file}")

    await small.close()
    await large.close()


if __name__ == "__main__":
    print(f"\n🔬 Gemma4 MLX Benchmark: {MODEL_SMALL} vs {MODEL_LARGE}\n")
    asyncio.run(compare_models())
