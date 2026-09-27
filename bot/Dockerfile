# Optional: only needed if you don't host on a Python egg (Pterodactyl) but in Docker.
FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY bot.py .
COPY vanta_bot ./vanta_bot
# state.json is written next to bot.py; mount /app/data and set STATE_FILE=/app/data/state.json to keep it
ENV PYTHONUNBUFFERED=1
CMD ["python", "bot.py"]
