# Task 043: local-inference sidecar image (local testing stub).
# Production swaps real models without C# changes.
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py ./

# Non-root runtime user.
RUN useradd --uid 1654 --create-home appuser \
    && chown -R appuser:appuser /app
USER 1654

EXPOSE 8000

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]
