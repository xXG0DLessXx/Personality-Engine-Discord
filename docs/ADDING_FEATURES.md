# Adding features — cookbook

Practical guide for adding Discord-side features (slash commands, buttons, modals, message handling), outbound Discord actions, DB entities, and background jobs to the Character Engine project.

For the legacy monolith documentation see `ARCHITECTURE.md` / `BUSINESS_LOGIC.md` / `CONNECTORS.md` / `CONCURRENCY.md` (archived; describes `_src.old/`). This document and `CLAUDE.md` describe the current architecture.

---

## Project layout

7 projects on .NET 10, in `src/CharacterEngineDiscord.slnx`:

```
CharacterEngineDiscord.Core           — Options POCOs, IDiscordLogger, IClock,
  ▲                                     TraceId, UserFriendlyException; only
  │                                     Microsoft.Extensions.* deps
  │
  ├─ CharacterEngineDiscord.DataAccess        — AppDbContext + DiscordGuild
  │                                              + EF migrations; snake_case;
  │                                              EnableRetryOnFailure
  │
  ├─ CharacterEngineDiscord.Contracts         — IDomainMessage / IRequestMessage
  │                                              / ICommandMessage records;
  │                                              zero deps (BCL only)
  │
  └─ CharacterEngineDiscord.Messaging         — RabbitMQ.Client 7.x wrapper:
                                                 publisher / dispatchers /
                                                 consumers / topology /
                                                 type registry; refs Core +
                                                 Contracts

CharacterEngineDiscord.DiscordBot     — exe; refs Core+DataAccess+Contracts
  (Discord.Net 3.19)                    +Messaging+Discord.Net+Serilog

CharacterEngineDiscord.Server         — exe; refs Core+DataAccess+Contracts
  (no Discord.Net)                      +Messaging+Hangfire+Serilog

CharacterEngineDiscord.Tests          — xUnit v3 + FluentAssertions; refs
                                         Core+Contracts+Messaging+DataAccess
                                         +Server+DiscordBot
```

**Bot vs Server:** Bot owns Discord I/O (gateway events, REST, slash registration). Server owns business logic + DB writes + scheduled work. Both read DB directly via `AppDbContext`; only Server writes (single source of truth). They communicate exclusively through RabbitMQ.

---

## Message bus topology

| Object | Name | Type | Durable |
|---|---|---|---|
| Exchange (Bot→Server) | `ce.requests` | direct | yes |
| Exchange (Server→Bot) | `ce.commands` | direct | yes |
| Queue (Server consumes) | `ce.requests.q` | — | yes |
| Queue (Bot consumes) | `ce.commands.q` | — | yes |
| DLX | `ce.deadletter` | fanout | yes |
| DLQ | `ce.deadletter.q` | — | yes |

Routing keys: `ce.request.{kebab}` and `ce.command.{kebab}`, where `{kebab}` is the type name with `Request`/`Command` suffix stripped, kebab-cased. Bindings use wildcards `ce.request.*` / `ce.command.*`.

Properties: persistent delivery (`delivery_mode=2`), JSON content-type, `correlation_id`=`TraceId`, `message_id`=`MessageId.ToString()`, `type`=class name, `headers["x-message-version"]`=`MessageVersion`.

Manual ack throughout. Unknown message type → `BasicNackAsync(requeue=false)` → DLX. Handler exception → `BasicNackAsync(requeue=true)` → retry. Successful handle → `BasicAckAsync`.

Code: `Messaging/Topology/`, `Messaging/Configuration/RabbitMqOptions.cs`, `Messaging/Internals/`.

---

## End-to-end interaction flow (slash command example)

```
Discord Gateway
  │
  ▼
  SocketSlashCommand          (received by DiscordBot)
  │
  ▼
  CeSlashCommandEventForwarder
  │
  ├─ ICeWatchDog.Check                      ← rate-limit BEFORE Defer
  │   └─ if !IsAllowed:
  │        ├─ interaction.RespondAsync(reason, ephemeral: true)
  │        ├─ if JustBlocked: IDiscordLogger.ReportAsync(Warning) — admin notify
  │        └─ return
  │
  ├─ interaction.DeferAsync(ephemeral: false) ← MUST be inside 3-sec ack window
  │
  └─ ICeMessagePublisher.PublishRequestAsync(SlashCommandInvokedRequest)
                                                                     │
ce.requests → Bot ────────────────────────────────────────────────► ce.requests.q
                                                                     │
                                                                     ▼
                                                  Server CeRequestConsumerHostedService
                                                    │
                                                    ▼
                                                  CeRequestDispatcher
                                                    (per-message scope via IServiceScopeFactory)
                                                    │
                                                    ▼
                                                  ICeRequestHandler<SlashCommandInvokedRequest>
                                                    i.e. CeSlashCommandRouter
                                                    │
                                                    ├─ try { switch on CommandName → per-cmd handler }
                                                    └─ catch UserFriendlyException → publish ephemeral
                                                       RespondToInteractionCommand & return (ack)
                                                    │
                                                    ▼
                                                  Per-command handler
                                                    │
                                                    └─ ICeMessagePublisher.PublishCommandAsync(
                                                          RespondToInteractionCommand)
                                                                     │
                                  ce.commands ◄───────────────────────┘
                                       │
                                       ▼
                                     ce.commands.q
                                       │
                                       ▼
                  Bot CeCommandConsumerHostedService
                  → CeCommandDispatcher
                  → ICeCommandHandler<RespondToInteractionCommand>
                  → POST https://discord.com/api/v10/webhooks/{appId}/{token}
                                       │
                                       ▼
                                User sees message
```

---

## Cookbook 1 — add a new slash command

Example: `/foo` that responds with text built from user input.

### Step A: extend the registrar

`src/CharacterEngineDiscord.DiscordBot/Hosting/CeSlashCommandRegistrarHostedService.cs` — add to the bulk-overwrite array:

```csharp
var ping = new SlashCommandBuilder()
    .WithName("ping")
    .WithDescription("Health-check.")
    .Build();

var foo = new SlashCommandBuilder()
    .WithName("foo")
    .WithDescription("Does the foo.")
    .AddOption("name", ApplicationCommandOptionType.String, "What to foo", isRequired: true)
    .Build();

await guild.BulkOverwriteApplicationCommandsAsync(new ApplicationCommandProperties[] { ping, foo });
```

`BulkOverwrite` is idempotent — Discord computes the diff. Safe to call on every startup.

### Step B: write the Server-side handler

`src/CharacterEngineDiscord.Server/RequestHandlers/FooSlashCommandHandler.cs`:

```csharp
namespace CharacterEngineDiscord.Server.RequestHandlers;

internal class FooSlashCommandHandler
{
    private readonly ICeMessagePublisher _publisher;
    private readonly ILogger<FooSlashCommandHandler> _logger;

    public FooSlashCommandHandler(ICeMessagePublisher publisher, ILogger<FooSlashCommandHandler> logger)
    {
        _publisher = publisher;
        _logger = logger;
    }

    public virtual async Task HandleAsync(SlashCommandInvokedRequest request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            throw new UserFriendlyException("`name` option is required.");
        }

        var content = $"Foo'd **{name}**!";

        await _publisher.PublishCommandAsync(new RespondToInteractionCommand
        {
            TraceId = request.TraceId,
            MessageId = Guid.NewGuid(),
            OccurredAt = DateTime.UtcNow,
            ApplicationId = request.ApplicationId,
            InteractionToken = request.InteractionToken,
            Content = content,
            IsEphemeral = false,
            OriginGuildId = request.GuildId,
            OriginChannelId = request.ChannelId,
        }, cancellationToken);

        _logger.LogInformation("[{Trace}] /foo handled for user {UserId}", request.TraceId, request.UserId);
    }
}
```

`internal` (not sealed) + `virtual HandleAsync` so tests can subclass-stub.

### Step C: register the handler

`src/CharacterEngineDiscord.Server/Extensions/ServerServiceCollectionExtensions.cs` inside `AddCharacterEngineServer`:

```csharp
services.AddScoped<FooSlashCommandHandler>();
```

### Step D: add a case to the router

`src/CharacterEngineDiscord.Server/Routing/CeSlashCommandRouter.cs` — inject `_foo` in ctor and add to switch:

```csharp
case "foo":
    await _foo.HandleAsync(request, cancellationToken);
    break;
```

### Step E: tests (recommended)

- Router test: case dispatches to FooSlashCommandHandler.
- Handler test: missing `name` option throws `UserFriendlyException`; valid input publishes the right command.

Use the same hand-stub pattern as `Tests/Server/Routing/CeSlashCommandRouterTests.cs` (no NSubstitute needed).

---

## Cookbook 2 — add a new interaction type (button / modal / select-menu)

Example: button with `customId="confirm-shutdown"`.

### Step A: define the request contract

`src/CharacterEngineDiscord.Contracts/Requests/ButtonClickedRequest.cs`:

```csharp
public sealed record ButtonClickedRequest : MessageEnvelope, IRequestMessage
{
    public required string CustomId { get; init; }
    public required ulong ApplicationId { get; init; }
    public required ulong GuildId { get; init; }
    public required ulong ChannelId { get; init; }
    public required ulong UserId { get; init; }
    public required string Username { get; init; }
    public required ulong InteractionId { get; init; }
    public required string InteractionToken { get; init; }
}
```

### Step B: register the message in both projects

In `AddCharacterEngineDiscordBot` and `AddCharacterEngineServer`:

```csharp
services.RegisterMessage<ButtonClickedRequest>();
```

Symmetric registration is required: the type registry must know about every request and command on both ends so the deserializer can route by `BasicProperties.Type` correctly.

### Step C: write the forwarder

`src/CharacterEngineDiscord.DiscordBot/EventForwarders/CeButtonEventForwarder.cs`:

```csharp
internal sealed class CeButtonEventForwarder
{
    private readonly ICeMessagePublisher _publisher;
    private readonly ICeWatchDog _watchDog;
    private readonly IDiscordLogger _discordLogger;
    private readonly ILogger<CeButtonEventForwarder> _logger;

    // ctor omitted for brevity

    public async Task OnButtonExecutedAsync(SocketMessageComponent interaction)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        // Same rate-limit prelude as the slash forwarder
        var decision = _watchDog.Check(interaction.User.Id);
        if (!decision.IsAllowed)
        {
            try { await interaction.RespondAsync(BuildRateLimitMessage(decision), ephemeral: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "..."); }
            if (decision.JustBlocked) { await NotifyAdminAsync(interaction, decision); }
            return;
        }

        var traceId = TraceId.New();

        try { await interaction.DeferAsync(ephemeral: true); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Trace}] DeferAsync failed for button {CustomId}", traceId, interaction.Data.CustomId);
            return;
        }

        await _publisher.PublishRequestAsync(new ButtonClickedRequest
        {
            TraceId = traceId,
            MessageId = Guid.NewGuid(),
            OccurredAt = DateTime.UtcNow,
            CustomId = interaction.Data.CustomId,
            ApplicationId = interaction.ApplicationId,
            GuildId = interaction.GuildId.GetValueOrDefault(),
            ChannelId = interaction.ChannelId.GetValueOrDefault(),
            UserId = interaction.User.Id,
            Username = interaction.User.Username,
            InteractionId = interaction.Id,
            InteractionToken = interaction.Token,
        });
    }
}
```

Register as singleton in `AddCharacterEngineDiscordBot`.

### Step D: subscribe in the host

`src/CharacterEngineDiscord.DiscordBot/Hosting/CeDiscordBotHostedService.cs`:

```csharp
public async Task StartAsync(CancellationToken ct)
{
    _client.ButtonExecuted += _buttonForwarder.OnButtonExecutedAsync;
    // existing subscriptions...
}

public async Task StopAsync(CancellationToken ct)
{
    _client.ButtonExecuted -= _buttonForwarder.OnButtonExecutedAsync;
    // existing unsubscriptions...
}
```

### Step E: handle on Server side

`src/CharacterEngineDiscord.Server/RequestHandlers/ButtonClickedRequestHandler.cs` — `internal class : ICeRequestHandler<ButtonClickedRequest>`. Switch on `CustomId` if you have multiple buttons; throw `UserFriendlyException` for permission denials.

If you have multiple button-driven flows, add a router (`CeButtonRouter`) similar to `CeSlashCommandRouter` and register that as the `ICeRequestHandler<ButtonClickedRequest>`.

### Modals and select menus

Same pattern — different Discord.Net event (`ModalSubmitted` / `SelectMenuExecuted`) and different request shape (`ModalSubmittedRequest` carries the form components dictionary; `SelectMenuRequest` carries the selected values). Forwarder + DI registration + Server handler — identical structure.

---

## Cookbook 3 — add a new outbound command (Server → Bot Discord-action)

Example: `SendTextMessageCommand` for posting to an arbitrary channel without an interaction context (e.g. scheduled announcement, broadcast).

### Step A: define the contract

`src/CharacterEngineDiscord.Contracts/Commands/SendTextMessageCommand.cs`:

```csharp
public sealed record SendTextMessageCommand : MessageEnvelope, ICommandMessage
{
    public required ulong ChannelId { get; init; }
    public required string Content { get; init; }
    public ulong? OriginGuildId { get; init; }    // for logs
}
```

Include **everything the Bot needs to execute** — Bot must not query DB to fill in fields. Server is the single source of truth; the command carries the resolved data.

### Step B: register on both sides

```csharp
services.RegisterMessage<SendTextMessageCommand>();
```

### Step C: write the Bot-side handler

`src/CharacterEngineDiscord.DiscordBot/CommandHandlers/SendTextMessageCommandHandler.cs`:

```csharp
internal sealed class SendTextMessageCommandHandler : ICeCommandHandler<SendTextMessageCommand>
{
    private readonly DiscordShardedClient _client;
    private readonly ILogger<SendTextMessageCommandHandler> _logger;

    public SendTextMessageCommandHandler(DiscordShardedClient client, ILogger<SendTextMessageCommandHandler> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task HandleAsync(SendTextMessageCommand command, CancellationToken cancellationToken)
    {
        if (_client.LoginState != LoginState.LoggedIn)
        {
            throw new InvalidOperationException("Discord client not yet logged in");
            // requeue on next poll
        }

        var channel = _client.GetChannel(command.ChannelId) as IMessageChannel;
        if (channel is null)
        {
            _logger.LogWarning(
                "[{Trace}] Channel {ChannelId} not in cache; dropping",
                command.TraceId, command.ChannelId);
            return;        // ack-and-discard — no point requeueing; Bot can't reach the channel
        }

        try
        {
            await channel.SendMessageAsync(command.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Trace}] SendMessageAsync failed", command.TraceId);
            throw;        // requeue
        }
    }
}
```

Register in `AddCharacterEngineDiscordBot`:

```csharp
services.AddScoped<ICeCommandHandler<SendTextMessageCommand>, SendTextMessageCommandHandler>();
```

### Step D: publish from the Server

Anywhere with `ICeMessagePublisher`:

```csharp
await _publisher.PublishCommandAsync(new SendTextMessageCommand
{
    TraceId = TraceId.New(),
    MessageId = Guid.NewGuid(),
    OccurredAt = DateTime.UtcNow,
    ChannelId = someChannelId,
    Content = "Daily report ready.",
    OriginGuildId = someGuildId,
}, cancellationToken);
```

### Choosing the Discord-side mechanism

| Need | Use | Notes |
|---|---|---|
| Reply to interaction (slash/button/modal already deferred) | `RespondToInteractionCommand` → REST `POST /webhooks/{appId}/{token}` | Use `IHttpClientFactory`; interaction tokens auth themselves |
| Plain message in a channel | `SendTextMessageCommand` → `channel.SendMessageAsync` | Bot needs cache + bot-token auth |
| Edit a message Bot owns | `EditMessageCommand` → `channel.ModifyMessageAsync` | Resolve message id from DB |
| Show modal | Cannot be a follow-up — modal must be sent as the FIRST response. Means Server must signal Bot via a dedicated forwarder branch (TODO when first modal feature lands) |
| Webhook (character persona) | `*WebhookCommand` → `DiscordWebhookClient` | Cache the `DiscordWebhookClient` in a singleton storage (TODO when chat lands) |

Error policy:
- **404 (not found / token expired)** → log warning + return (ack). Requeueing won't help.
- **5xx / network blip** → throw → consumer requeues with `requeue=true`.
- **`LoginState != LoggedIn`** → throw to requeue; Bot will catch up after reconnect.

---

## Cookbook 4 — add a new DB entity

### Step A: write the model

`src/CharacterEngineDiscord.DataAccess/Models/<Area>/Foo.cs`:

```csharp
public sealed class Foo
{
    public ulong Id { get; init; }
    public required string Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

`init` for fields fixed at insert; `set` for fields the app updates. `required` for fields that must be supplied at construction.

### Step B: write the configuration

`src/CharacterEngineDiscord.DataAccess/Configurations/<Area>/FooConfiguration.cs`:

```csharp
internal sealed class FooConfiguration : IEntityTypeConfiguration<Foo>
{
    public void Configure(EntityTypeBuilder<Foo> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();   // for Discord snowflakes
        builder.Property(f => f.Name).IsRequired().HasMaxLength(100);
        builder.Property(f => f.CreatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();
        builder.Property(f => f.UpdatedAt).HasDefaultValueSql("now()").ValueGeneratedOnAdd();

        // Optional: soft-delete
        // builder.HasQueryFilter(f => !f.IsDeleted);
    }
}
```

`ApplyConfigurationsFromAssembly` in `AppDbContext.OnModelCreating` picks it up automatically — no manual registration.

### Step C: add the DbSet

`src/CharacterEngineDiscord.DataAccess/AppDbContext.cs`:

```csharp
public DbSet<Foo> Foos => Set<Foo>();
```

### Step D: generate a migration

```
dotnet ef migrations add Add<Name> --project src/CharacterEngineDiscord.DataAccess
```

`DesignTimeAppDbContextFactory` provides a stub connection string for design-time, so no `--startup-project` is needed and no live DB connection occurs. Commit all files in `Migrations/` (the migration .cs, the .Designer.cs, and the updated `AppDbContextModelSnapshot.cs`).

### Step E: use it in a Server handler

```csharp
internal class SomeRequestHandler : ICeRequestHandler<SomeRequest>
{
    private readonly AppDbContext _db;
    // ...

    public async Task HandleAsync(SomeRequest request, CancellationToken ct)
    {
        var existing = await _db.Foos.FirstOrDefaultAsync(f => f.Id == request.SomeId, ct);
        if (existing is null) { throw new UserFriendlyException("Foo not found."); }
        existing.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync(ct);
    }
}
```

`AppDbContext` is Scoped — never inject it into a Singleton. Singleton handlers (forwarders) open a scope via `IServiceScopeFactory.CreateAsyncScope()`.

---

## Cookbook 5 — add a Hangfire job

### Recurring job

```csharp
// In any Server-side service that needs scheduling, e.g. ServerServiceCollectionExtensions.AddCharacterEngineServer
RecurringJob.AddOrUpdate<DailyMetricsJob>(
    recurringJobId: "daily-metrics",
    methodCall: job => job.RunAsync(CancellationToken.None),
    cronExpression: Cron.Daily());
```

Job class:

```csharp
internal sealed class DailyMetricsJob
{
    private readonly AppDbContext _db;
    private readonly IDiscordLogger _discordLogger;

    public DailyMetricsJob(AppDbContext db, IDiscordLogger discordLogger) { /* … */ }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // ... query DB, build report
        await _discordLogger.ReportAsync(new DiscordLogEntry { Title = "...", Message = "..." }, LogLevel.Information, cancellationToken);
    }
}
```

Place under `src/CharacterEngineDiscord.Server/Jobs/`. Hangfire creates a scope per job invocation, so `AppDbContext` resolution works automatically.

### Fire-and-forget job

```csharp
_backgroundJobClient.Enqueue<SendBroadcastJob>(job => job.RunAsync(broadcastId, CancellationToken.None));
```

`IBackgroundJobClient` is registered by `AddCharacterEngineHangfire`.

---

## Patterns and invariants — must-follow

### `DeferAsync` BEFORE `PublishRequestAsync`

Discord gives **3 seconds** to ack an interaction. RabbitMQ publish can take 5–500 ms. If publish goes first, any transient blip eats the ack window and the user sees a permanently-loading interaction.

**Always:** rate-limit check → DeferAsync (or RespondAsync for short replies / modals) → publish.

### Manual ack, persistent messages

Never auto-ack. Successful handler → `BasicAckAsync`. Handler exception → `BasicNackAsync(requeue: true)`. Unknown message type / poison → `BasicNackAsync(requeue: false)` → DLX.

### At-least-once delivery

Same message may be delivered twice in edge cases (consumer crash before ack, requeue on transient failure). For now rely on natural idempotency (e.g. upsert); for critical operations a `MessageId`-keyed dedup table is **TODO Phase 4** (markers in `Messaging/Internals/CeRequestConsumerHostedService.cs` and `CeCommandConsumerHostedService.cs`).

### `UserFriendlyException` for known business errors

```csharp
if (caller.Permissions < Required) { throw new UserFriendlyException("You don't have permission."); }
if (notFound) { throw new UserFriendlyException("Channel not found."); }
```

`CeSlashCommandRouter` catches it, publishes an ephemeral `RespondToInteractionCommand` with the exception's message, and acks the queue message. **Any other exception** propagates to the consumer and gets requeued — so don't use general `Exception`s for expected validation failures or you'll create infinite retry loops.

### `IDiscordLogger.ReportAsync` for admin notifications

Both Bot and Server inject `IDiscordLogger`. Both implementations publish `ReportLogToAdminChannelCommand` to the bus — single executor (`ReportLogToAdminChannelCommandHandler` in Bot) calls `channel.SendMessageAsync`. Never call Discord API directly from Server.

Severity routing:
- `LogLevel.Error` / `Critical` → `AdminOptions.ErrorsChannelId`
- `LogLevel.Information` / `Warning` / `Debug` / `Trace` → `AdminOptions.LogsChannelId`

### `ICeWatchDog.Check` is automatic

Forwarders run `Check` before `DeferAsync`. Don't add custom rate-limiting — extend `RateLimitOptions` if defaults need tuning. The dog skips `AdminOptions.OwnerUserIds` automatically.

### `TraceId` propagation

```csharp
var traceId = TraceId.New();    // at the forwarder entry
// ...
await _publisher.PublishRequestAsync(new SomeRequest { TraceId = traceId, /* ... */ });
```

Receiving handler propagates the same `TraceId` into any outbound commands and into log statements (`_logger.LogInformation("[{Trace}] ...", req.TraceId, ...)`). End-to-end correlation across Bot + Server logs uses the trace id as the join key.

### `ApplicationId` in interaction requests

`SlashCommandInvokedRequest` / `ButtonClickedRequest` / `ModalSubmittedRequest` always carry `ApplicationId`. Server has no Discord credentials and resolves nothing about the bot identity itself. Bot fills it from `interaction.ApplicationId`.

### Bot writes nothing to DB; Server writes everything

Bot may *read* DB (e.g. cache lookups). Bot must not write — if a forwarder needs to persist something, it publishes a request and lets a Server handler write. Single source of truth keeps cross-process consistency simple.

---

## Testing patterns

`xUnit v3` + `FluentAssertions 7.2.0`. No `NSubstitute` yet — hand-rolled stubs are explicit and avoid an extra dependency.

### Stub publisher

```csharp
private sealed class StubMessagePublisher : ICeMessagePublisher
{
    public List<IRequestMessage> PublishedRequests { get; } = new();
    public List<ICommandMessage> PublishedCommands { get; } = new();

    public Task PublishRequestAsync<T>(T msg, CancellationToken ct = default) where T : IRequestMessage
    {
        PublishedRequests.Add(msg);
        return Task.CompletedTask;
    }

    public Task PublishCommandAsync<T>(T msg, CancellationToken ct = default) where T : ICommandMessage
    {
        PublishedCommands.Add(msg);
        return Task.CompletedTask;
    }
}
```

### Stub a `virtual` handler

When testing a router, subclass the handler:

```csharp
private sealed class StubbablePingHandler : PingSlashCommandHandler
{
    public StubbablePingHandler(ICeMessagePublisher pub) : base(pub, NullLogger<PingSlashCommandHandler>.Instance) { }
    public Func<SlashCommandInvokedRequest, CancellationToken, Task>? StubBehavior { get; set; }
    public override Task HandleAsync(SlashCommandInvokedRequest req, CancellationToken ct)
        => StubBehavior?.Invoke(req, ct) ?? Task.CompletedTask;
}
```

That's why concrete handlers are `internal class`/`virtual HandleAsync` instead of `internal sealed`.

### Test clock

```csharp
private sealed class TestClock : IClock
{
    public DateTime UtcNow { get; set; } = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan ts) => UtcNow += ts;
}
```

Used in `CeWatchDogTests` — drives sliding window / block-expiry behaviour without `Thread.Sleep`.

### Loggers

Always `NullLogger<T>.Instance` from `Microsoft.Extensions.Logging.Abstractions`.

### What's hard to test (skip for now)

- **Forwarders** depend on Discord.Net concrete sealed types (`SocketSlashCommand`, `SocketGuild`) — can't mock without an interface extraction.
- **`DiscordShardedClient`-based command handlers** — same.
- **RabbitMQ publishers/consumers** — integration test with Testcontainers (TODO).
- **Hangfire jobs** — would need a real Postgres (TODO).

For these, integration tests with Testcontainers (`Testcontainers.PostgreSql`, `Testcontainers.RabbitMq`) are the right tool. Not added yet.

---

## Codestyle and naming

See `src/.editorconfig` for the full set. Highlights:

- **File-scoped namespaces.** Allman braces (control flow); end-of-line braces for object/collection/array initializers. Force braces on every if/else/for/while/etc.
- **`var` everywhere.** `_camelCase` private fields.
- **`I` prefix for interfaces, `T` prefix for type parameters.**
- **`Ce` prefix** for our custom services / framework-shaped infra: `CeWatchDog`, `CeSlashCommandRouter`, `CeRabbitConnection`, `CeMessagePublisher`. Indicates "we wrote this and own its semantics".
- **No prefix** for: contracts (records), Options POCOs, exceptions, entities, validators, DTOs, handlers — they're plain types named for what they hold/do.
- **Contracts naming:** inbound (Bot→Server) → `*Request`; outbound (Server→Bot) → `*Command`. Strict.
- **Routing keys:** `ce.request.{kebab}` / `ce.command.{kebab}`. The kebab part is the type name minus the `Request`/`Command` suffix, then PascalCase-to-kebab via `RoutingKeys.ForRequest(Type)` / `ForCommand(Type)`. Don't hand-write routing keys.

---

## What NOT to port from `_src.old/` as-is

| `_src.old` pattern | Replacement in new project |
|---|---|
| `BotConfig` static class with `File.ReadAllLines` per access | `IOptions<*>` POCOs in `Core/Configuration/` |
| `CharacterEngineBot.DiscordClient` `public static` global | inject `DiscordShardedClient` from DI |
| `IntegrationsHub` static singletons | will be a proper Singleton + interface (TODO when first integration module is ported) |
| `BackgroundWorker` self-rolled with 4 hardcoded loops | Hangfire recurring jobs (one job per loop, scheduled) |
| `*Helper` static utility classes with side effects | regular DI-injected services with the `Ce` prefix if they own behaviour |
| `WatchDog` static + DB persistence + 3-stage Warning tier | `CeWatchDog` (in-memory now; DB persistence is a TODO; Warning tier is a TODO; current behaviour is binary Allowed/Blocked) |
| `MetricsWriter` static + `Metric` table | TODO when needed; Hangfire job + `IMetricsWriter` interface |
| Manual exception swallowing | `UserFriendlyException` for expected; let the rest propagate to consumer for requeue |
| Per-class `_log = LogManager.GetCurrentClassLogger()` (NLog) | `ILogger<T>` injected via DI; `IDiscordLogger` for admin notifications |
| Mixed Code-First / DB-First / separate Migrator project | Code-First only; migrations live with the DbContext |

The legacy code remains in `_src.old/` as a reference for behavioural intent during feature-by-feature porting. Read it for the *what*, not the *how*.
