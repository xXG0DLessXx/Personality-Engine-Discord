# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Character Engine is a sharded Discord bot that lets a server spawn AI-driven character webhooks backed by external chat platforms (CharacterAI, SakuraAI, OpenRouter, ChubAI). The project is mid-rewrite: the current code in `src/` is the new architecture (split frontend/backend with RabbitMQ); the old monolith lives in `_src.old/` as a read-only archive.

**Current architecture (in `src/`):** sharded Discord bot (Discord.Net 3.19), .NET 10, EF Core 10 + PostgreSQL, RabbitMQ.Client 7. Two runner exes — **DiscordBot** (frontend) and **Server** (backend) — communicating via RabbitMQ; both share `Core` and `DataAccess` libraries.

**Legacy monolith (in `_src.old/`):** preserved for behaviour reference during feature-by-feature porting. Do not edit; do not run; do not copy patterns wholesale (most are anti-patterns we're explicitly replacing).

For the practical guide on how to add features, see [`docs/ADDING_FEATURES.md`](docs/ADDING_FEATURES.md).

The four older `docs/*.md` files (`ARCHITECTURE.md`, `BUSINESS_LOGIC.md`, `CONNECTORS.md`, `CONCURRENCY.md`) describe the legacy monolith — they have a banner at the top and are kept for reference only.

## Submodules

Three Git submodules under `submodules/` host the upstream HTTP clients (CharacterAI, SakuraAI, OpenRouter). They are **empty in fresh clones**:

```bash
git submodule update --init --recursive
```

The current `src/` code does **not** reference them yet — they will be wired in when chat-integration features are ported. The legacy `_src.old/CharacterEngineDiscord.Modules.csproj` references them by relative path; do not delete or move the `submodules/` folder.

## Common commands

```bash
# Build everything
dotnet build src/CharacterEngineDiscord.slnx

# Run all unit tests
dotnet test src/CharacterEngineDiscord.slnx

# Run Bot or Server locally (assumes Postgres + RabbitMQ are reachable)
dotnet run --project src/CharacterEngineDiscord.DiscordBot/CharacterEngineDiscord.DiscordBot.csproj
dotnet run --project src/CharacterEngineDiscord.Server/CharacterEngineDiscord.Server.csproj

# Full stack via Docker (Postgres + RabbitMQ + Bot + Server)
docker compose -f src/docker-compose.yml up --build

# EF Core migrations (DesignTimeAppDbContextFactory provides a stub connection string,
# so no --startup-project is needed and no live DB connection occurs at design time)
dotnet ef migrations add <Name> --project src/CharacterEngineDiscord.DataAccess
dotnet ef database update      --project src/CharacterEngineDiscord.DataAccess
```

`CeDatabaseMigrationHostedService` applies pending migrations on Server startup (fail-fast). Manual `database update` is only needed when working entirely offline against a local DB.

## Required runtime configuration

Two layers, both per-runner:

1. **`src/CharacterEngineDiscord.{DiscordBot,Server}/Settings/appsettings.json`** — tracked default values; `Settings/appsettings.Development.json` is gitignored for local override. Sections:
   - `Bot` (Bot only): `Token`, `PlayingStatus`
   - `Discord` (Bot only): `MessageCacheSize`, `ConnectionTimeoutMs`
   - `Admin` (both): `GuildId`, `InviteLink`, `LogsChannelId`, `ErrorsChannelId`, `OwnerUserIds`
   - `ConnectionStrings:Default` (both): standard `Host=...;Username=...;Password=...;Database=...`
   - `RabbitMq` (both): `Host`, `Port`, `Username`, `Password`, `VirtualHost`, `PrefetchCount`, `RequestedHeartbeatSec`
   - `Messages` (Server only): `DefaultMessagesFormatFile`, `DefaultSystemPromptFile`, `DefaultAvatarFile` — file names resolved against `AppContext.BaseDirectory/Settings/` by `MessagesOptionsPostConfigure`
   - `RateLimit` (Bot only): `PerWindow`, `WindowSeconds`, `FirstBlockMinutes`, `SecondBlockHours`
   - `Emoji` (Bot only): per-platform emoji strings
   - `Serilog` (both): standard `MinimumLevel`/`Enrich`/`WriteTo` shape
2. **`.env`** at repo root (loaded by `docker-compose`): `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `RABBITMQ_USER`, `RABBITMQ_PASSWORD`. The compose file maps these into `Section__Key` env vars (`ConnectionStrings__Default`, `RabbitMq__Username`, etc.) which override `appsettings.json` at runtime.

Configuration sources are layered in `Program.cs`: `appsettings.json` → `appsettings.{Environment}.json` → environment variables → command-line.

## Architecture

### Solution layout (7 projects)

| Project | Role |
|---|---|
| `CharacterEngineDiscord.Core` | Options POCOs (`BotOptions`, `DiscordOptions`, `AdminOptions`, `MessagesOptions`, `RateLimitOptions`, `EmojiOptions`), validators, `IDiscordLogger` + `DiscordLogEntry`, `IClock` + `SystemClock`, `TraceId`, `UserFriendlyException`. Only `Microsoft.Extensions.*` deps. |
| `CharacterEngineDiscord.DataAccess` | `AppDbContext`, `DiscordGuild` entity (`Id/Name/OwnerId/OwnerUsername/MemberCount/IconUrl/Joined/JoinedAt/LeftAt/CreatedAt/UpdatedAt` — soft-delete via `Joined`+`LeftAt` with global query filter on `Joined`), EF migrations, `CeDatabaseMigrationHostedService` (fail-fast). EF Core 10 + Npgsql + `EFCore.NamingConventions` for snake_case + `EnableRetryOnFailure(5, 30s)`. |
| `CharacterEngineDiscord.Contracts` | Zero-deps records: `IDomainMessage`, `IRequestMessage`, `ICommandMessage` markers; `MessageEnvelope` abstract base (`TraceId`, `MessageId`, `OccurredAt`, `MessageVersion`); concrete `*Request` (Bot→Server) and `*Command` (Server→Bot) records. |
| `CharacterEngineDiscord.Messaging` | RabbitMQ.Client 7.x wrapper: `RabbitMqOptions`, `CeRabbitConnection` (singleton, lazy connect, automatic recovery enabled), `CeRabbitTopology` + `CeRabbitInfrastructureHostedService` (declares exchanges/queues/bindings + DLX on startup), `CeJsonMessageSerializer` (System.Text.Json + type registry), `CeMessagePublisher` (singleton long-lived `IChannel`), `ICeRequestHandler<>`/`ICeCommandHandler<>` generic interfaces, `CeRequestDispatcher`/`CeCommandDispatcher` (per-message DI scope), consumer hosted services. |
| `CharacterEngineDiscord.DiscordBot` | exe (Discord.Net 3.19). Owns Discord I/O. Hosting: `CeDiscordBotHostedService` (login + event subs), `CeSlashCommandRegistrarHostedService` (idempotent `BulkOverwriteApplicationCommandsAsync` on admin guild). Forwarders translate gateway events to messages: `CeSlashCommandEventForwarder`, `CeGuildLifecycleEventForwarder`. Command handlers execute Discord actions: `RespondToInteractionCommandHandler` (REST followup), `ReportLogToAdminChannelCommandHandler` (channel.SendMessageAsync). Rate limiter: `CeWatchDog` + `CeWatchDogCleanupHostedService`. Logger: `CeDiscordLogger : IDiscordLogger` (publisher-based). |
| `CharacterEngineDiscord.Server` | exe (no Discord.Net). Owns business logic + DB writes + scheduled jobs. `CeSlashCommandRouter` switches on `CommandName` → per-command handlers (`PingSlashCommandHandler`, future `*SlashCommandHandler`s). Guild-lifecycle persisters: `GuildJoinedRequestHandler`, `GuildLeftRequestHandler`. Logger: `CeDiscordLogger : IDiscordLogger` (also publisher-based — Server has no Discord.Net). Hangfire 1.8 (LGPL-3.0) wired via `AddCharacterEngineHangfire` for future scheduled jobs in `Jobs/`. |
| `CharacterEngineDiscord.Tests` | xUnit v3 (3.2) + FluentAssertions 7.2 (last open-source major before 8.x went commercial). Hand-rolled stubs (no NSubstitute yet). 64 tests. |

Dependencies: `Core` → no project refs; `DataAccess` → `Core`; `Contracts` → no project refs; `Messaging` → `Core` + `Contracts`; `DiscordBot` → `Core` + `DataAccess` + `Contracts` + `Messaging`; `Server` → same as DiscordBot minus Discord.Net deps; `Tests` → all of the above except DesignTime factory.

### Refactor history

Major commits (chronological):

| Commit | Phase |
|---|---|
| `3e1fcde` | Archive monolith as `_src.old/`; scaffold .NET 10 solution |
| `24eda69` | Phase 2: bot reaches Ready-State (DataAccess + Bootstrap + tests) |
| `0335fa4` | Phase 1 (split): introduce Contracts + Messaging; rename exe → DiscordBot; add Server; Docker |
| `d3a70a7` | Phase 2 (split): /ping end-to-end via RabbitMQ |
| `002e0ec` | Phase 3 (split): GuildLifecycle + IDiscordLogger migrated to message bus |
| `5c6d393` | Step 1: bootstrap hardening (EF retry, RMQ recovery, DI scope validation) |
| `b1ded52` | Step 2: UserFriendlyException + global catch in CeSlashCommandRouter |
| `325ed92` | Step 3: Hangfire integration in Server (OSS-only) |
| `1973fbe` | Step 4: CeWatchDog rate limiter |

### Message bus

Two direct exchanges (`ce.requests` / `ce.commands`), two durable queues, one fanout DLX (`ce.deadletter` + `ce.deadletter.q`). Routing keys: `ce.request.{kebab}` / `ce.command.{kebab}` (kebab from class name minus `Request`/`Command` suffix). Manual ack throughout, persistent messages (`delivery_mode=2`). `BasicProperties` carries `correlation_id`=`TraceId`, `message_id`=`MessageId`, `type`=class name, `headers["x-message-version"]`. Type registry in `CeJsonMessageSerializer` is bootstrapped via `services.RegisterMessage<T>()` calls (must be symmetric: Bot and Server register the same set).

See `docs/ADDING_FEATURES.md` for the end-to-end interaction flow diagram and step-by-step recipes.

## Conventions worth preserving

- **`AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`** is set as the first line of `CeDatabaseMigrationHostedService.StartAsync` (Server-side, before any DbContext is opened). DB code stores `DateTime.Now` (local kind) and relies on this. Don't introduce `DateTime.UtcNow` for stored timestamps without auditing all comparisons. (`OccurredAt` in message envelopes is UTC — that's a separate, in-flight contract concern, not stored in DB.)
- **Compile-time safety:** every project enables `Nullable`, treats **`CS8509` (non-exhaustive switch) as error**, and silences `CS8524`. When you `switch` on an enum, exhaustive matching is required (no default arm needed if all values are covered). Configured in `src/Directory.Build.props`.
- **Codestyle in `src/.editorconfig`:** file-scoped namespaces, Allman braces (control flow) / end-of-line braces (initializers), force braces on every if/else/etc., var everywhere, `_camelCase` private fields, `I` prefix interfaces, `T` prefix type parameters. **`Ce` prefix** for our custom services (`CeWatchDog`, `CeMessagePublisher`, `CeSlashCommandRouter`, etc.); **no prefix** for contracts / records / entities / exceptions / Options / handlers.
- **Contracts naming:** inbound (Bot→Server) is always `*Request`; outbound (Server→Bot) is always `*Command`. Strict.
- **`DeferAsync` BEFORE `PublishRequestAsync`** in any interaction forwarder. Discord allows 3 seconds to ack; RabbitMQ publish can take longer than that under load. Forwarder structure: rate-limit check → DeferAsync → publish.
- **Manual ack everywhere.** Successful handler → `BasicAckAsync`. Handler exception → `BasicNackAsync(requeue: true)`. Unknown message type → `BasicNackAsync(requeue: false)` → DLX. **TODO Phase 4:** dedup by `MessageEnvelope.MessageId` (markers in both consumer hosted services).
- **`UserFriendlyException`** for known business errors that should be shown to the user verbatim. `CeSlashCommandRouter` catches it, publishes an ephemeral `RespondToInteractionCommand`, and acks — no requeue. Any *other* exception requeues (use this pattern only for transient / programming bugs).
- **`IDiscordLogger.ReportAsync`** for all admin-channel notifications. Both Bot and Server inject `IDiscordLogger`; both implementations publish `ReportLogToAdminChannelCommand` to the bus — single executor (`ReportLogToAdminChannelCommandHandler` in Bot) actually calls `channel.SendMessageAsync`. Severity ≥ Error → `ErrorsChannelId`; otherwise → `LogsChannelId`.
- **`ICeWatchDog.Check`** runs in every interaction forwarder before `DeferAsync`. It's automatic; don't add custom rate-limiting. Owners listed in `AdminOptions.OwnerUserIds` are bypassed. Admin notification fires exactly once per ban transition (`RateLimitDecision.JustBlocked`). Persistence of blocks across bot restarts is a TODO documented in `CeWatchDog.cs`.
- **`TraceId.New()`** at the entry of each forwarder; propagate through `MessageEnvelope.TraceId` for end-to-end log correlation.
- **DI scope validation:** Bot and Server both enable `ValidateScopes = true` + `ValidateOnBuild = true` via `builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ... }))`. **Important:** `builder.Services.Configure<ServiceProviderOptions>(...)` is **silently ignored** by `HostApplicationBuilder` — the options are captured at construction time, not re-resolved at `Build()`. Always use `ConfigureContainer` for this.
- **Bot does not write to DB.** Reads are fine; writes go through the bus and are executed by Server. Single source of truth simplifies cross-process consistency.
- **All slash commands are guild-scoped, not global** (registered via `guild.BulkOverwriteApplicationCommandsAsync`). Global commands have a 1-hour propagation delay; we don't use them.
