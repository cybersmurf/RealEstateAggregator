#!/usr/bin/env python3
"""
Příprava a validace CZ datasetů z Hugging Face pro fine-tuning.

Použití:
    # Stáhnout a připravit saillab/alpaca-czech-cleaned:
    python prepare_dataset.py --dataset saillab/alpaca-czech-cleaned --output data/cz_train.jsonl

    # Smíchat více datasetů:
    python prepare_dataset.py \
        --dataset saillab/alpaca-czech-cleaned \
        --dataset hynky/czech-justice-summ-alpaca-short \
        --output data/cz_mixed.jsonl \
        --max-samples 30000

    # Přidat vlastní JSONL soubor:
    python prepare_dataset.py \
        --dataset saillab/alpaca-czech-cleaned \
        --local data/my_custom_data.jsonl \
        --output data/cz_combined.jsonl

    # Validace existujícího datasetu:
    python prepare_dataset.py --validate data/cz_train.jsonl
"""

import argparse
import json
import random
from pathlib import Path


ALPACA_REQUIRED_FIELDS = {"instruction", "output"}
ALPACA_OPTIONAL_FIELDS = {"input"}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Příprava CZ datasetů pro fine-tuning")
    parser.add_argument("--dataset", action="append", default=[],
                        metavar="HF_DATASET",
                        help="HF dataset ID (lze použít vícekrát)")
    parser.add_argument("--hf-split", default="train",
                        help="Split datasetu (default: train)")
    parser.add_argument("--local", action="append", default=[],
                        metavar="JSONL_FILE",
                        help="Lokální JSONL soubor(y) k přidání")
    parser.add_argument("--output", default="data/cz_train.jsonl",
                        help="Výstupní JSONL soubor")
    parser.add_argument("--max-samples", type=int, default=None,
                        help="Max počet příkladů (náhodný výběr)")
    parser.add_argument("--min-output-len", type=int, default=20,
                        help="Min délka odpovědi ve znacích (default: 20)")
    parser.add_argument("--max-output-len", type=int, default=2000,
                        help="Max délka odpovědi ve znacích (default: 2000)")
    parser.add_argument("--validate", metavar="JSONL_FILE",
                        help="Validovat existující JSONL soubor a zobrazit statistiky")
    parser.add_argument("--seed", type=int, default=42)
    return parser.parse_args()


def load_hf_dataset_records(dataset_id: str, split: str) -> list[dict]:
    """Načte HF dataset a převede na seznam Alpaca záznamů."""
    from datasets import load_dataset

    print(f"  → Stahuji {dataset_id} (split={split})...")
    ds = load_dataset(dataset_id, split=split)
    print(f"    Načteno {len(ds):,} příkladů, sloupce: {ds.column_names}")

    records = []
    for row in ds:
        record = normalize_record(row)
        if record:
            records.append(record)

    print(f"    Použitelných záznamů: {len(records):,} ({len(records)/len(ds)*100:.1f}%)")
    return records


def normalize_record(row: dict) -> dict | None:
    """
    Normalizuje záznam do Alpaca formátu {instruction, input, output}.
    Zvládá různé schémata z různých datasetů.
    """
    # Přímý Alpaca formát
    if "instruction" in row and "output" in row:
        return {
            "instruction": str(row.get("instruction", "")).strip(),
            "input": str(row.get("input", "") or "").strip(),
            "output": str(row.get("output", "")).strip(),
        }

    # Chat formát: messages = [{role, content}]
    if "messages" in row:
        messages = row["messages"]
        user_msgs = [m["content"] for m in messages if m.get("role") == "user"]
        assistant_msgs = [m["content"] for m in messages if m.get("role") == "assistant"]
        if user_msgs and assistant_msgs:
            return {
                "instruction": user_msgs[0].strip(),
                "input": "",
                "output": assistant_msgs[0].strip(),
            }

    # Question-answer formát
    if "question" in row and "answer" in row:
        return {
            "instruction": str(row["question"]).strip(),
            "input": "",
            "output": str(row["answer"]).strip(),
        }

    # Text formát (pre-training styl)
    if "text" in row:
        text = str(row["text"]).strip()
        if len(text) > 100:
            return {
                "instruction": "Pokračuj v textu:",
                "input": text[:200],
                "output": text[200:],
            }

    return None


def load_local_jsonl(path: str) -> list[dict]:
    """Načte záznamy z lokálního JSONL souboru."""
    print(f"  → Načítám lokální soubor: {path}")
    records = []
    with open(path, "r", encoding="utf-8") as f:
        for i, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
                record = normalize_record(row)
                if record:
                    records.append(record)
            except json.JSONDecodeError as e:
                print(f"    [WARN] Řádek {i}: JSON chyba – {e}")

    print(f"    Načteno {len(records):,} záznamů")
    return records


def filter_records(
    records: list[dict],
    min_output_len: int = 20,
    max_output_len: int = 2000,
) -> list[dict]:
    """Filtruje záznamy podle kvality."""
    filtered = []
    stats = {"too_short": 0, "too_long": 0, "empty_instruction": 0, "ok": 0}

    for r in records:
        output_len = len(r.get("output", ""))
        instruction = r.get("instruction", "").strip()

        if not instruction:
            stats["empty_instruction"] += 1
            continue
        if output_len < min_output_len:
            stats["too_short"] += 1
            continue
        if output_len > max_output_len:
            stats["too_long"] += 1
            continue

        filtered.append(r)
        stats["ok"] += 1

    print(f"  → Filtrování: {stats['ok']:,} OK, "
          f"{stats['too_short']:,} příliš krátké, "
          f"{stats['too_long']:,} příliš dlouhé, "
          f"{stats['empty_instruction']:,} bez instrukce")
    return filtered


def validate_jsonl(path: str) -> None:
    """Zobrazí statistiky o existujícím datasetu."""
    print(f"\nValidace: {path}")
    print("-" * 50)

    records = []
    errors = 0
    with open(path, "r", encoding="utf-8") as f:
        for i, line in enumerate(f, 1):
            try:
                r = json.loads(line.strip())
                records.append(r)
            except json.JSONDecodeError:
                errors += 1

    if not records:
        print("[ERROR] Žádné záznamy!")
        return

    total = len(records)
    instruction_lens = [len(r.get("instruction", "")) for r in records]
    output_lens = [len(r.get("output", "")) for r in records]
    has_input = sum(1 for r in records if r.get("input", "").strip())

    print(f"Celkem záznamů:   {total:,}")
    print(f"JSON chyby:       {errors}")
    print(f"Se 'input':       {has_input:,} ({has_input/total*100:.1f}%)")
    print(f"\nInstrukce délka:  min={min(instruction_lens)}, "
          f"avg={sum(instruction_lens)//total}, max={max(instruction_lens)}")
    print(f"Odpověď délka:    min={min(output_lens)}, "
          f"avg={sum(output_lens)//total}, max={max(output_lens)}")

    # Ukázka prvních 3 záznamů
    print("\nUkázka (první 3):")
    for i, r in enumerate(records[:3], 1):
        print(f"\n  [{i}] Instrukce: {r.get('instruction', '')[:80]}...")
        print(f"       Odpověď:   {r.get('output', '')[:80]}...")


def main():
    args = parse_args()
    random.seed(args.seed)

    # ── Validační mód ────────────────────────────────────────────────────────
    if args.validate:
        validate_jsonl(args.validate)
        return

    if not args.dataset and not args.local:
        print("[ERROR] Zadejte alespoň jeden --dataset nebo --local soubor.")
        print("Viz: python prepare_dataset.py --help")
        return

    # ── Načtení dat ──────────────────────────────────────────────────────────
    all_records: list[dict] = []

    for hf_id in args.dataset:
        records = load_hf_dataset_records(hf_id, args.hf_split)
        all_records.extend(records)

    for local_path in args.local:
        records = load_local_jsonl(local_path)
        all_records.extend(records)

    print(f"\nCelkem načteno: {len(all_records):,} záznamů")

    # ── Filtrování ────────────────────────────────────────────────────────────
    all_records = filter_records(all_records, args.min_output_len, args.max_output_len)

    # ── Náhodný výběr ────────────────────────────────────────────────────────
    if args.max_samples and len(all_records) > args.max_samples:
        all_records = random.sample(all_records, args.max_samples)
        print(f"  → Náhodný výběr: {args.max_samples:,} příkladů")

    random.shuffle(all_records)

    # ── Uložení ──────────────────────────────────────────────────────────────
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    with open(output_path, "w", encoding="utf-8") as f:
        for record in all_records:
            f.write(json.dumps(record, ensure_ascii=False) + "\n")

    print(f"\n✅ Uloženo {len(all_records):,} příkladů → {output_path}")
    print(f"   Spusť fine-tuning: python finetune_unsloth.py --dataset {output_path}")


if __name__ == "__main__":
    main()
