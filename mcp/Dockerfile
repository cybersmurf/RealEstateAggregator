FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY server.py .

# SSE transport v Dockeru
ENV TRANSPORT=sse
ENV PORT=8002
ENV API_BASE_URL=http://realestate-api:8080

EXPOSE 8002

CMD ["python", "server.py"]
