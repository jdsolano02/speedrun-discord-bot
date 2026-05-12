# 🏆 Speedrun Discord Bot CR

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Discord.Net](https://img.shields.io/badge/Discord.Net-3.19-5865F2?logo=discord)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite)
![Docker](https://img.shields.io/badge/Docker-2496ED?logo=docker)

A robust and scalable Discord bot designed to monitor, track, and notify National Records (NRs) and Personal Bests (PBs) for the Costa Rican speedrunning community.

## 🏗️ Architecture
Built following **Clean Architecture** and **SOLID** principles to ensure maintainability:

* **Domain:** Framework-independent core logic and entities (`RunRecord`, `GuildConfig`, `TrackedGame`).
* **Application:** Business use cases (`RegisterUser`, `CheckForNewRecords`) and service interfaces.
* **Infrastructure:** Implementation of data persistence (EF Core + SQLite), Speedrun.com API integration, and Discord command engine.
* **Worker:** A background service executing an asynchronous scanning loop with **Intelligent Adaptive Throttling** to respect API rate limits.

## ✨ Key Features
* **Multi-Guild Support:** Server-agnostic configuration allows each Discord server to set its own announcement channels and "Data Helper" roles via `/setup`.
* **Adaptive Scanning:** Intelligent "gearbox" logic that adjusts scan speeds based on API response health and success streaks.
* **Relational Persistence:** Uses SQLite with indexing for fast ranking lookups and concurrent data access.
* **Modern UI/UX:** Advanced Slash Commands with smart autocomplete for games, categories, and runners.

## 🚀 Deployment
The project is containerized for easy deployment on Linux VPS or platforms like Railway.

```bash
# Build the image
docker build -t speedrun-bot .

# Run with persistent volume for data
docker run -d --name speedrun-bot -v ./data:/app/data speedrun-bot

## 👨‍💻 Developer & Support
Developed by Jose Solano (realxones / jdsolano02).

If you find this project useful and want to help keep the bot running for the community, consider supporting the development:

Support & Tips: https://streamelements.com/realxones/tip
Socials: https://linktr.ee/xones
GitHub: https://github.com/jdsolano02

Pura vida speedrunning! 🇨🇷