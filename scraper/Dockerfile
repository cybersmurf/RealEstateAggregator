FROM python:3.12-slim

WORKDIR /app

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends gcc libpq-dev \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt ./
RUN --mount=type=cache,target=/root/.cache/pip \
    pip install -r requirements.txt

# Playwright browsers (pro JS-heavy weby jako Premia Reality)
# Prohlížeč NESMÍ skončit v cache mountu — obsah cache mountu se do image
# nedostane a Playwright pak za běhu nemá co spustit (tak to bylo 2026-09-14).
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN playwright install chromium --with-deps

COPY . .

EXPOSE 8001

CMD ["python", "run_api.py"]
