# 🏆 Speedrun Discord Bot CR

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Discord.Net](https://img.shields.io/badge/Discord.Net-3.13-5865F2?logo=discord)
![SQLite](https://img.shields.io/badge/SQLite-003B57?logo=sqlite)
![Docker](https://img.shields.io/badge/Docker-2496ED?logo=docker)

Un bot de Discord robusto y distribuido, diseñado para monitorizar, registrar y notificar Récords Nacionales (NRs) y Personal Bests (PBs) de la comunidad de Speedrun.

## 🏗️ Arquitectura del Proyecto
Este proyecto está desarrollado bajo los principios de **Clean Architecture** y **SOLID**, garantizando un alto nivel de mantenibilidad y escalabilidad. La solución se divide en 4 capas principales:

* **Domain:** Contiene las entidades principales (`RunRecord`, `GuildConfig`, `TrackedGame`) independientes de cualquier framework.
* **Application:** Casos de uso (`RegisterUser`, `CheckForNewRecords`) e interfaces (Contratos de repositorios y APIs).
* **Infrastructure:** Implementación de persistencia con **Entity Framework Core (SQLite)**, integración con la API de Speedrun.com y el motor de comandos de Discord.
* **Worker:** Servicio de fondo (Background Service) que ejecuta el escaneo masivo asíncrono implementando un sistema de *Save State* y limitación de peticiones adaptativa (Rate Limiting).

## ✨ Características Principales
* **Agnóstico de Servidor (Multi-Guild):** Sistema SaaS que permite a cada servidor de Discord configurar sus propios canales de anuncios y roles de administrador mediante un asistente in-app (`/setup`).
* **Sincronización Asíncrona:** Escaneo constante de la API de Speedrun.com sin bloquear el hilo principal del bot de Discord.
* **Persistencia Relacional:** Transición de archivos planos a SQLite para garantizar concurrencia, indexación rápida y escalabilidad.
* **Comandos Slash (UI/UX):** Autocompletado inteligente de juegos, categorías y usuarios registrados.

## 🚀 Despliegue (Deployment)
El proyecto está completamente dockerizado para ser desplegado en cualquier entorno Linux/VPS.
```bash
docker build -t speedrun-bot .
docker run -d --name speedrun-bot-container -v ./data:/app/data speedrun-bot