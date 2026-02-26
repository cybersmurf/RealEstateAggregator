#!/usr/bin/env python3
"""
Fine-tuning LLM pro češtinu pomocí Unsloth + QLoRA.

Použití:
    python finetune_unsloth.py \
        --model unsloth/gemma-2-9b-bnb-4bit \
        --dataset data/cz_train.jsonl \
        --output output/my-cz-gemma \
        --epochs 2 \
        --lora-rank 32

Doporučené modely (4-bit kvantizace, ready na Unsloth):
    - unsloth/gemma-2-9b-bnb-4bit       (~9B, 16GB VRAM)
    - unsloth/llama-3.2-3b-bnb-4bit     (~3B, 8GB VRAM)
    - unsloth/llama-3.1-8b-bnb-4bit     (~8B, 14GB VRAM)
    - unsloth/Qwen2.5-7B-bnb-4bit       (~7B, 12GB VRAM)
    - unsloth/mistral-7b-v0.3-bnb-4bit  (~7B, 12GB VRAM)
"""

import argparse
import json
import os
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Fine-tune LLM pro češtinu")
    parser.add_argument("--model", default="unsloth/llama-3.2-3b-bnb-4bit",
                        help="HuggingFace model ID nebo lokální cesta")
    parser.add_argument("--dataset", default="data/cz_train.jsonl",
                        help="Cesta k JSONL datasetu nebo HF dataset ID")
    parser.add_argument("--output", default="output/my-cz-model",
                        help="Výstupní adresář pro uložení modelu")
    parser.add_argument("--epochs", type=int, default=2,
                        help="Počet trénovacích epoch (default: 2)")
    parser.add_argument("--lora-rank", type=int, default=32,
                        help="LoRA rank (16=rychlejší, 64=kvalitnější)")
    parser.add_argument("--lora-alpha", type=int, default=16,
                        help="LoRA alpha (doporučeno: rank/2)")
    parser.add_argument("--max-seq-len", type=int, default=2048,
                        help="Maximální délka sekvence (default: 2048)")
    parser.add_argument("--batch-size", type=int, default=2,
                        help="Batch size na zařízení")
    parser.add_argument("--grad-accum", type=int, default=8,
                        help="Gradient accumulation steps (efektivní batch = batch_size * grad_accum)")
    parser.add_argument("--lr", type=float, default=2e-4,
                        help="Learning rate (default: 2e-4)")
    parser.add_argument("--hf-dataset", default=None,
                        help="Načíst přímo z HF, např. saillab/alpaca-czech-cleaned")
    parser.add_argument("--hf-split", default="train",
                        help="Split HF datasetu (default: train)")
    parser.add_argument("--eval-split", type=float, default=0.05,
                        help="Procento dat pro evaluaci (default: 5%%)")
    return parser.parse_args()


def load_local_dataset(path: str):
    """Načte JSONL soubor s Alpaca formátem."""
    from datasets import Dataset

    records = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                records.append(json.loads(line))
    return Dataset.from_list(records)


def load_hf_dataset(dataset_id: str, split: str):
    """Načte dataset přímo z Hugging Face Hub."""
    from datasets import load_dataset
    return load_dataset(dataset_id, split=split)


def format_alpaca_prompt(example: dict) -> dict:
    """
    Formátuje příklad do Alpaca instrukčního formátu.

    Vstup: {"instruction": "...", "input": "...", "output": "..."}
    Výstup: {"text": "<s>[INST] ... [/INST] ... </s>"}
    """
    instruction = example.get("instruction", "").strip()
    context = example.get("input", "").strip()
    response = example.get("output", "").strip()

    if context:
        prompt = (
            f"Níže je instrukce, která popisuje úkol, spolu s dalším vstupem. "
            f"Napište odpověď, která úkol splní.\n\n"
            f"### Instrukce:\n{instruction}\n\n"
            f"### Vstup:\n{context}\n\n"
            f"### Odpověď:\n{response}"
        )
    else:
        prompt = (
            f"Níže je instrukce, která popisuje úkol. "
            f"Napište odpověď, která úkol splní.\n\n"
            f"### Instrukce:\n{instruction}\n\n"
            f"### Odpověď:\n{response}"
        )

    return {"text": prompt + tokenizer_eos}


# Globální EOS token (bude nastaven po načtení tokenizéru)
tokenizer_eos = "</s>"


def main():
    args = parse_args()

    print(f"[INFO] Načítám model: {args.model}")
    print(f"[INFO] LoRA rank={args.lora_rank}, alpha={args.lora_alpha}")
    print(f"[INFO] Max sekvence: {args.max_seq_len}")

    # ── 1. Načtení modelu a tokenizéru ──────────────────────────────────────
    from unsloth import FastLanguageModel
    import torch

    model, tokenizer = FastLanguageModel.from_pretrained(
        model_name=args.model,
        max_seq_length=args.max_seq_len,
        dtype=None,             # auto (bfloat16 na podporovaných GPU)
        load_in_4bit=True,      # QLoRA – nutné pro 8-16 GB VRAM
    )

    global tokenizer_eos
    tokenizer_eos = tokenizer.eos_token or "</s>"

    # ── 2. Aplikace LoRA ─────────────────────────────────────────────────────
    model = FastLanguageModel.get_peft_model(
        model,
        r=args.lora_rank,
        target_modules=[
            "q_proj", "k_proj", "v_proj", "o_proj",
            "gate_proj", "up_proj", "down_proj",
        ],
        lora_alpha=args.lora_alpha,
        lora_dropout=0.05,
        bias="none",
        use_gradient_checkpointing="unsloth",  # šetří ~30% VRAM
        random_state=42,
        use_rslora=False,
    )

    print(f"[INFO] Trénované parametry: {model.num_parameters(only_trainable=True):,}")

    # ── 3. Načtení datasetu ──────────────────────────────────────────────────
    if args.hf_dataset:
        print(f"[INFO] Načítám HF dataset: {args.hf_dataset} (split={args.hf_split})")
        dataset = load_hf_dataset(args.hf_dataset, args.hf_split)
    elif Path(args.dataset).exists():
        print(f"[INFO] Načítám lokální dataset: {args.dataset}")
        dataset = load_local_dataset(args.dataset)
    else:
        raise FileNotFoundError(
            f"Dataset '{args.dataset}' nenalezen. "
            f"Použijte --hf-dataset nebo spusťte nejdřív prepare_dataset.py"
        )

    print(f"[INFO] Celkem příkladů: {len(dataset):,}")

    # Formátování do Alpaca šablony
    dataset = dataset.map(format_alpaca_prompt, batched=False)

    # Train/eval split
    if args.eval_split > 0:
        splits = dataset.train_test_split(test_size=args.eval_split, seed=42)
        train_dataset = splits["train"]
        eval_dataset = splits["test"]
        print(f"[INFO] Train: {len(train_dataset):,}, Eval: {len(eval_dataset):,}")
    else:
        train_dataset = dataset
        eval_dataset = None
        print(f"[INFO] Train: {len(train_dataset):,}, Eval: vypnuto")

    # ── 4. Trénování ─────────────────────────────────────────────────────────
    from trl import SFTTrainer
    from transformers import TrainingArguments
    from unsloth import is_bfloat16_supported

    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)

    training_args = TrainingArguments(
        output_dir=str(output_dir / "checkpoints"),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        gradient_accumulation_steps=args.grad_accum,
        warmup_steps=50,
        learning_rate=args.lr,
        fp16=not is_bfloat16_supported(),
        bf16=is_bfloat16_supported(),
        logging_steps=20,
        optim="adamw_8bit",            # 8-bit Adam – šetří VRAM
        weight_decay=0.01,
        lr_scheduler_type="cosine",
        seed=42,
        save_strategy="epoch",
        evaluation_strategy="epoch" if eval_dataset else "no",
        load_best_model_at_end=eval_dataset is not None,
        report_to="none",              # vypnout W&B/TensorBoard pokud není nastaveno
    )

    trainer = SFTTrainer(
        model=model,
        tokenizer=tokenizer,
        train_dataset=train_dataset,
        eval_dataset=eval_dataset,
        dataset_text_field="text",
        max_seq_length=args.max_seq_len,
        dataset_num_proc=2,
        packing=False,                 # False = stabilnější, True = rychlejší
        args=training_args,
    )

    print(f"[INFO] Spouštím trénování ({args.epochs} epochy)...")
    trainer_stats = trainer.train()

    print(f"[INFO] Trénování dokončeno!")
    print(f"       Čas: {trainer_stats.metrics.get('train_runtime', 0):.0f}s")
    print(f"       Samples/sec: {trainer_stats.metrics.get('train_samples_per_second', 0):.1f}")

    # ── 5. Uložení modelu ────────────────────────────────────────────────────
    # Uložit LoRA adapter (malý, ~50–200 MB)
    adapter_path = output_dir / "lora_adapter"
    model.save_pretrained(str(adapter_path))
    tokenizer.save_pretrained(str(adapter_path))
    print(f"[INFO] LoRA adapter uložen: {adapter_path}")

    # Merge LoRA do plného modelu + uložit pro GGUF konverzi
    print("[INFO] Mergování LoRA do base modelu (nutné pro GGUF export)...")
    model.save_pretrained_merged(
        str(output_dir / "merged"),
        tokenizer,
        save_method="merged_16bit",
    )
    print(f"[INFO] Merged model uložen: {output_dir / 'merged'}")

    # Přímý export do GGUF (volitelné – vyžaduje llama.cpp v PATH)
    print("[INFO] Exportuji do GGUF (Q4_K_M kvantizace)...")
    try:
        model.save_pretrained_gguf(
            str(output_dir / "gguf"),
            tokenizer,
            quantization_method="q4_k_m",
        )
        print(f"[INFO] GGUF model uložen: {output_dir / 'gguf'}")
        print(f"\n[HOTOVO] Spusť export_to_ollama.sh pro vytvoření Ollama modelu:")
        print(f"         bash export_to_ollama.sh {output_dir}/gguf my-cz-model")
    except Exception as e:
        print(f"[WARN] GGUF export selhal: {e}")
        print(f"       Spusť ručně: bash export_to_ollama.sh {output_dir}/merged my-cz-model")


if __name__ == "__main__":
    main()
