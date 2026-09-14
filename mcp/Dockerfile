FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN --mount=type=cache,target=/root/.cache/pip \
    pip install -r requirements.txt

COPY server.py .

ENV TRANSPORT=sse
ENV PORT=8002
ENV API_BASE_URL=http://realestate-api:8080

EXPOSE 8002

CMD ["python", "server.py"]
