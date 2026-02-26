#!/usr/bin/env bash
# export_to_ollama.sh
# Konvertuje fine-tuned model do GGUF a vytvoří Ollama model.
#
# Použití:
#   bash export_to_ollama.sh <model_dir> <ollama_name> [quantization]
#
# Příklady:
#   bash export_to_ollama.sh output/my-cz-gemma/gguf my-cz-gemma-q4
#   bash export_to_ollama.sh output/my-cz-gemma/merged my-cz-gemma-q5 Q5_K_M
#
# Kvantizace (rychlost vs. kvalita):
#   Q4_K_M  – doporučeno, dobrý kompromis (~4.5 GB pro 7B)
#   Q5_K_M  – kvalitnější, větší soubor (~5.5 GB pro 7B)
#   Q8_0    – téměř bez ztráty kvality (~8 GB pro 7B)
#   F16     – plná přesnost, největší soubor

set -euo pipefail

# ── Argumenty ────────────────────────────────────────────────────────────────
MODEL_DIR="${1:?Použití: $0 <model_dir> <ollama_name> [quantization]}"
OLLAMA_NAME="${2:?Zadejte název pro Ollama model}"
QUANTIZATION="${3:-Q4_K_M}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GGUF_OUTPUT="${MODEL_DIR}/model-${QUANTIZATION}.gguf"
MODELFILE="${MODEL_DIR}/Modelfile"

echo "══════════════════════════════════════════════"
echo "  Export do Ollama"
echo "  Model dir:   ${MODEL_DIR}"
echo "  Ollama název: ${OLLAMA_NAME}"
echo "  Kvantizace:  ${QUANTIZATION}"
echo "══════════════════════════════════════════════"

# ── Kontrola závislostí ───────────────────────────────────────────────────────
command -v ollama >/dev/null 2>&1 || {
    echo "[ERROR] Ollama není nainstalována!"
    echo "        Instalace: curl -fsSL https://ollama.ai/install.sh | sh"
    exit 1
}

# ── Krok 1: GGUF konverze (pokud input není GGUF) ─────────────────────────────
if [[ "${MODEL_DIR}" == *".gguf" ]] || ls "${MODEL_DIR}"/*.gguf 2>/dev/null | head -1 | grep -q ".gguf"; then
    # Již máme GGUF soubor
    GGUF_FILE=$(ls "${MODEL_DIR}"/*.gguf 2>/dev/null | head -1 || echo "${MODEL_DIR}")
    if [[ -f "${MODEL_DIR}" ]]; then
        GGUF_FILE="${MODEL_DIR}"
    fi
    echo "[INFO] Používám existující GGUF: ${GGUF_FILE}"
else
    # Konverze z HuggingFace formátu
    echo "[INFO] Konvertuji do GGUF (${QUANTIZATION})..."

    # Zkusit llama.cpp v PATH nebo typických umístěních
    LLAMACPP_PYTHON=""
    for candidate in \
        "$(command -v llama-convert 2>/dev/null)" \
        "${HOME}/llama.cpp/convert_hf_to_gguf.py" \
        "${HOME}/llama.cpp/convert.py" \
        "/opt/llama.cpp/convert_hf_to_gguf.py" \
        "/opt/llama.cpp/convert.py"
    do
        if [[ -f "${candidate}" ]] || command -v "${candidate}" 2>/dev/null; then
            LLAMACPP_PYTHON="${candidate}"
            break
        fi
    done

    if [[ -z "${LLAMACPP_PYTHON}" ]]; then
        echo "[WARN] llama.cpp convert skript nenalezen."
        echo "       Pokud nebylo GGUF exportováno přímo Unsloths (save_pretrained_gguf),"
        echo "       nainstalujte llama.cpp:"
        echo ""
        echo "         git clone https://github.com/ggerganov/llama.cpp"
        echo "         cd llama.cpp && pip install -r requirements.txt"
        echo ""
        echo "       Pak spusťte:"
        echo "         python llama.cpp/convert_hf_to_gguf.py ${MODEL_DIR} --outfile ${GGUF_OUTPUT} --outtype ${QUANTIZATION,,}"
        exit 1
    fi

    python3 "${LLAMACPP_PYTHON}" \
        "${MODEL_DIR}" \
        --outfile "${GGUF_OUTPUT}" \
        --outtype "${QUANTIZATION,,}"

    GGUF_FILE="${GGUF_OUTPUT}"
    echo "[INFO] GGUF vytvořeno: ${GGUF_FILE}"
    echo "[INFO] Velikost: $(du -sh "${GGUF_FILE}" | cut -f1)"
fi

# ── Krok 2: Vytvoření Modelfile ───────────────────────────────────────────────
echo "[INFO] Vytvářím Modelfile: ${MODELFILE}"

cat > "${MODELFILE}" << EOF
FROM ${GGUF_FILE}

# Czech language system prompt
SYSTEM """Jsi užitečný asistent hovořící česky. Odpovídáš přesně, stručně a zdvořile.
Always respond in Czech unless explicitly asked to use another language."""

# Parametry generování
PARAMETER temperature 0.7
PARAMETER top_p 0.9
PARAMETER top_k 40
PARAMETER repeat_penalty 1.1
PARAMETER num_ctx 4096

# Chat šablona (přizpůsob modelu)
# TEMPLATE "{{ if .System }}system: {{ .System }}{{ end }}{{ if .Prompt }}user: {{ .Prompt }}{{ end }}assistant: "
EOF

echo "[INFO] Modelfile vytvořen"

# ── Krok 3: Registrace v Ollama ───────────────────────────────────────────────
echo "[INFO] Registruji model v Ollama jako '${OLLAMA_NAME}'..."
ollama create "${OLLAMA_NAME}" -f "${MODELFILE}"

echo ""
echo "══════════════════════════════════════════════"
echo "  ✅ Model úspěšně vytvořen!"
echo ""
echo "  Test:"
echo "    ollama run ${OLLAMA_NAME} \"Ahoj, jak se máš?\""
echo ""
echo "  Pro použití v RealEstate projektu:"
echo "    # scraper/config/settings.yaml:"
echo "    # llm:"
echo "    #   provider: ollama"
echo "    #   model: ${OLLAMA_NAME}"
echo "    #   base_url: http://localhost:11434"
echo "══════════════════════════════════════════════"
