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
* **Worker:** A background service executing an asynchronous scanning loop with **Intelligent Adaptive Throttling** to respect API rate limits, alongside the **Prestige Engine** for background metric calculations.

## ✨ Key Features
* **Prestige Engine & Competitive Weighting:** Mathematically calculates the prestige of every run based on global percentiles (`1.0 - (WorldRank / TotalGlobalRunners)`) to generate highly accurate "Top Player" and "Top Run" leaderboards.
* **Self-Healing Database (Integrity Scanner):** Automatically detects and purges corrupted data, deleted leaderboards (404s), and Individual Levels (ILs) to maintain a pure "Full Game" database.
* **Bulletproof Anti-Spam Shield:** Advanced tracking logic that ensures Discord notifications are only triggered for genuinely new and improved times, preventing duplicate link loops.
* **Adaptive Scanning:** Intelligent "gearbox" logic that dynamically adjusts scan speeds (threads and delays) based on API response health and success streaks.
* **Multi-Guild Support:** Server-agnostic configuration allows each Discord server to set its own announcement channels and "Data Helper" roles via `/setup`.
* **Advanced Command Suite:** Modern UI/UX with Slash Commands, smart autocomplete, and detailed stats tracking (e.g., `/game most_played`, `/game recent`, `/top players`).

## 🚀 Deployment
The project is containerized for easy deployment on Linux VPS or cloud platforms like Railway.

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