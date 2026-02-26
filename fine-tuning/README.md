# Fine-tuning LLM pro češtinu

Dokumentace k fine-tuningu jazykového modelu pro český jazyk pomocí CZ datasetů z Hugging Face.

## Obsah adresáře

```
fine-tuning/
├── README.md               ← tento soubor (přehled, datasety, workflow)
├── finetune_unsloth.py     ← kompletní skript (Unsloth + QLoRA)
├── prepare_dataset.py      ← příprava a validace CZ datasetů
├── export_to_ollama.sh     ← konverze GGUF + vytvoření Modelfile
└── requirements.txt        ← závislosti
```

---

## Přehled workflow

```
HuggingFace Dataset
    ↓ prepare_dataset.py
Alpaca formát (JSONL)
    ↓ finetune_unsloth.py
LoRA adapter + merged model
    ↓ export_to_ollama.sh (llama.cpp convert)
GGUF soubor → Ollama model
```

---

## Požadavky

| Komponenta | Minimum | Doporučeno |
|---|---|---|
| GPU VRAM | 8 GB (3B model) | 16–24 GB (9B model) |
| RAM | 16 GB | 32 GB |
| Disk | 30 GB | 100 GB |
| Python | 3.10+ | 3.11 |
| CUDA | 11.8+ | 12.1+ |

> **Alternativy bez GPU**: Google Colab (free T4 15GB), RunPod (A100 $1.09/h), Vast.ai

---

## Doporučené CZ datasety

| Dataset | Příklady | Formát | Zaměření |
|---------|----------|--------|----------|
| [saillab/alpaca-czech-cleaned](https://huggingface.co/datasets/saillab/alpaca-czech-cleaned) | ~52 000 | Alpaca instruction/input/response | Obecný chat, instrukce |
| [pinzhenchen/alpaca-cleaned-cs](https://huggingface.co/datasets/pinzhenchen/alpaca-cleaned-cs) | ~52 000 | Alpaca cleaned | Instrukční tuning |
| [hynky/czech-justice-summ-alpaca-short](https://huggingface.co/datasets/hynky/czech-justice-summ-alpaca-short) | ~2 000 | Alpaca short | Shrnutí, právní chat |
| [masakberserk/czech-texts](https://huggingface.co/datasets/masakberserk/czech-texts) | Miliony tokenů | Text korpus | Pre-training / domain |
| gretelai/synthetic-czech-gpt-4 | Variabilní | Chat dialogy | Syntetizovaný GPT-4 |

### Kde najít více
- [Czech NLP datasety na HF](https://huggingface.co/datasets?language=cs)
- [r/LocalLLaMA – SOTA Czech LLM](https://www.reddit.com/r/LocalLLaMA/comments/1ap05xs/sota_llm_in_czech_language/)

---

## Kroky k fine-tunu

### 1. Připravte prostředí
```bash
cd fine-tuning
pip install -r requirements.txt
```

### 2. Připravte dataset
```bash
python prepare_dataset.py --dataset saillab/alpaca-czech-cleaned --output data/cz_train.jsonl
# Volitelně přidejte domain-specific data:
python prepare_dataset.py --dataset hynky/czech-justice-summ-alpaca-short --output data/cz_legal.jsonl --merge data/cz_train.jsonl
```

### 3. Spusťte fine-tuning
```bash
python finetune_unsloth.py \
  --model unsloth/gemma-2-9b-bnb-4bit \
  --dataset data/cz_train.jsonl \
  --output output/my-cz-gemma \
  --epochs 2 \
  --lora-rank 32
```

### 4. Exportujte do Ollama
```bash
bash export_to_ollama.sh output/my-cz-gemma my-cz-gemma-q4
# Nebo pro rychlejší kvantizaci Q5_K_M:
bash export_to_ollama.sh output/my-cz-gemma my-cz-gemma-q5 Q5_K_M
```

### 5. Otestujte
```bash
ollama run my-cz-gemma-q4 "Vysvětli mi rozdíl mezi zástavním právem a věcným břemenem."
```

---

## Tipy a upozornění

- **Overfitting**: Sledujte eval_loss – pokud roste zatímco train_loss klesá, snižte epochs nebo zvyšte dropout.
- **Mixování jazyků**: Pro zachování anglických schopností míchejte CZ data s anglickými (poměr 70:30 CZ:EN).
- **Rank LoRA**: Rank 16 stačí pro konverzaci; rank 64 pro silnější adaptaci na doménu.
- **Batch size**: Na 16GB VRAM použijte `per_device_train_batch_size=2` + `gradient_accumulation_steps=8`.
- **Flash Attention 2**: Unsloth automaticky zapíná FA2 – 2–5× rychlejší než vanilla attention.

---

## Integrace s RealEstate projektem

Fine-tuned model lze použít jako alternativu k OpenAI GPT-4 v AI analýzách inzerátů:

```yaml
# scraper/config/settings.yaml – přidej sekci:
llm:
  provider: ollama          # nebo "openai"
  model: my-cz-gemma-q5    # lokální fine-tuned model
  base_url: http://localhost:11434
```

Viz [src/RealEstate.Api/Services/AnalysisService.cs](../src/RealEstate.Api/Services/AnalysisService.cs) – stačí přepnout `OllamaBaseUrl` / `ModelName` v konfiguraci.
