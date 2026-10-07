# NetSdr Resilience and Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Додати до NetSdr стійкий клієнт керування `ResilientControlClient` (перепідключення, heartbeat, повтори з перейняттям запізнілих відповідей, колбек `ConnectionRestored`), спільний інтерфейс `INetSdrControlClient` і логування всієї бібліотеки через `Microsoft.Extensions.Logging` зі стабільними EventId.

**Architecture:** `ResilientControlClient` тримає один внутрішній `NetSdrControlClient` на TCP-з'єднання (`FaultOnTimeout = false`, internal `Supervised = true`) і пише в нього лише під замком лінії Wire, тож запит без відповіді блокує лінію, доки його відповідь не прийде або з'єднання не замінять. Наглядач, одна задача на клієнт, бачить втрату за `Completion` внутрішнього клієнта, перепідключається через pipeline Polly.Core і викликає колбек застосунку до публікації нового з'єднання. Простий клієнт отримує лише інтерфейс, логування і internal-гачки, що без налаштування нічого не змінюють; кожен компонент логує через свій internal клас `[LoggerMessage]`, а приймач UDP пише лише підсумки за інтервал.

**Tech Stack:** .NET 10, C# latest, Polly.Core 8.8.0, Microsoft.Extensions.Logging.Abstractions 10.0.12 (разом із генератором `[LoggerMessage]`), xUnit 2.9.3, Microsoft.Extensions.TimeProvider.Testing 10.10.0 (`FakeTimeProvider`, простір `Microsoft.Extensions.Time.Testing`), Microsoft.Extensions.Diagnostics.Testing 10.10.0 (`FakeLogger`, `FakeLogCollector`, простір `Microsoft.Extensions.Logging.Testing`).

**Spec:** `docs/superpowers/specs/2026-10-07-netsdr-resilience-logging-design.md` (далі "спека"; "спека 6.5" означає її розділ 6.5). Базова спека `2026-10-02-netsdr-framework-design.md` і спека ідентифікації `2026-10-02-netsdr-device-identification-design.md` дають решту контексту. Алгоритми, які спека описує покроково (6.5, 6.6, 7.1-7.7), план не переписує, а посилається на них.

## Global Constraints

- Усі проєкти `net10.0` через наявний `Directory.Build.props` (Nullable, ImplicitUsings, LangVersion latest, без `unsafe`); він не змінюється.
- `NetSdr.csproj` отримує рівно два `PackageReference`: `Polly.Core` 8.8.0 і `Microsoft.Extensions.Logging.Abstractions` 10.0.12.
- `NetSdr.Tests.csproj` отримує `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 і `Microsoft.Extensions.Diagnostics.Testing` 10.10.0. `NetSdr.Testing`, приклад Vega і його тести нових пакетів не отримують. `NetSdr.Testing` не логує.
- Жодного типу Polly в public чи protected сигнатурі. Без `Polly.Extensions`, `ConfigureTelemetry` і `TelemetryListener`. Налаштування стійкості лише числовими опціями `ResilientControlClientOptions`.
- `ILoggerFactory LoggerFactory` у `NetSdrControlClientOptions`, `DataReceiverOptions`, `IdentificationOptions`, `ResilientControlClientOptions`: за замовчуванням `NullLoggerFactory.Instance`, `null` дає `ArgumentNullException` там, де опції читаються (спека 3.1).
- Кожна опція `TimeProvider` internal (`NetSdrControlClientOptions`, `DataReceiverOptions`, `ResilientControlClientOptions`), за замовчуванням `TimeProvider.System`; `null` дає `ArgumentNullException` там само, де `LoggerFactory`. Так само internal `NetSdrControlClientOptions.Supervised` і `ResilientControlClientOptions.UseJitter`.
- Категорії логерів: `NetSdr.Control.NetSdrControlClient`, `NetSdr.Control.ResilientControlClient`, `NetSdr.Data.NetSdrDataReceiver`, `NetSdr.Identification.DeviceIdentity`, `NetSdr.Identification.DeviceCatalog` (без узагальненого аргументу).
- EventId: 1000-1099 `NetSdrControlClient`, 1100-1199 `ResilientControlClient`, 1200-1299 `NetSdrDataReceiver`, 1300-1399 ідентифікація. Id, EventName, рівень і шаблон повідомлення дослівно з таблиць спеки 3.2-3.5. Номери ніколи не перенумеровуються, нові лише додаються.
- Повідомлення генерує `[LoggerMessage]` у internal static partial класах `ControlClientLog`, `ResilientClientLog`, `DataReceiverLog`, `IdentificationLog`. Подія з рівнем, що залежить від `Supervised`, не має `Level` в атрибуті і приймає параметр `LogLevel level`. Виняток іде аргументом `Exception`, не текстом.
- Нічого не логується під жодним `_sync`. Hex кадру будується лише всередині `if (logger.IsEnabled(LogLevel.Trace))`. UDP не пише жодного запису про окремий пакет на жодному рівні. У стійкому клієнті немає виклику логера між захопленням Wire (`Wire.WaitAsync` / `Wire.Wait(0)`) і передачею його обміну в `StartExchange`.
- Логер створюється один раз на екземпляр (конструктор або `ConnectAsync`; для статичного `DeviceIdentity.ReadAsync` один раз на виклик).
- Типові значення `ResilientControlClientOptions`: `ResponseTimeout` 2 с, `LateReplyTimeout` 13 с, `CommandTimeout` 30 с, `HeartbeatInterval` 5 с, `ConnectTimeout` 5 с, `ReconnectAttempts` `int.MaxValue`, `UnsolicitedCapacity` 256, `UseJitter` `true`.
- Повтор команди: Polly `MaxRetryAttempts = 3` (4 спроби), `Delay = TimeSpan.Zero`, без jitter. Перепідключення: `DelayBackoffType.Exponential`, `Delay` 1 с, `MaxDelay` 30 с, `UseJitter` з опцій, щонайменше 1 с між початками спроб; стратегія додається лише при `ReconnectAttempts > 1`.
- `DataReceiverOptions.StatisticsLogInterval` за замовчуванням 10 с, `Timeout.InfiniteTimeSpan` вимикає; годинник читається раз на 256 датаграм, таймера немає. `DataPacketInfo` без `Timestamp` і `UtcTime`.
- Ламання сумісності прийнято (спека 4.6): жодних прокладок, `[Obsolete]` чи старих перевантажень.
- Ідентифікатори, коментарі, XML-документація, тексти винятків і логів англійською. Тексти винятків, які фіксує спека (6.5 кроки 0, 4a, 4e, 7; 6.6; 7.4; 7.6), дослівно.
- Тести лише на loopback із портом 0. Кожне очікування в тестах обмежене `Limits.Test` (5 с): тест падає, а не зависає.
- Перевірка задачі: `dotnet test NetSdr.sln --filter "FullyQualifiedName~<клас>"`; наприкінці кожної задачі `dotnet test NetSdr.sln` зелений повністю (базова лінія 346 тестів плюс нові).
- Документи з розділу 2.4 спеки вже оновлено разом зі спекою. Задача змінює їх лише тоді, коли її код мусить відійти від написаного.
- Каталог `.claude/` не чіпати. Кожне повідомлення коміту закінчується рядком `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

П'ять вхідних ситуацій, які спека має на увазі, але жоден тест її розділу 10 не перевіряє, від найімовірнішої. Кожна має тест у задачі-власнику.

1. Хост задано рядком, як у прикладі спеки 5.5 (`ConnectAsync("127.0.0.1", port)`): перепідключення йде тим самим шляхом через ім'я, а `LocalEndPoint` лишається IPv4, придатним для `DataOutputUdpAddress.For`. Тест `HostName_ReconnectsByName_EndPointsStayIPv4`, задача 8.
2. Багатогодинний обрив: десятки невдалих спроб із jitter не переповнюють затримку (`2^n` секунд виходить за `TimeSpan` близько 40-ї спроби), кожна затримка в межах 30 с, а повернення пристрою відновлює з'єднання без відмови. Тест `LongOutage_SeventyAttempts_DelayCappedThenRecovers`, задача 8.
3. Застосунок закривається, поки команди чекають на перепідключення, на admission або на відповідь: кожна завершується `ObjectDisposedException`, а не зависанням, `TimeoutException` чи `OperationCanceledException`; виклик після `DisposeAsync` кидає синхронно. Тест `Dispose_FailsWaitingCommands_WithObjectDisposed`, задача 8.
4. Колбек `ConnectionRestored` написаний без `async`: кидає синхронно або повертає `null` замість задачі. Провалюється лише ця спроба (`Phase = Restore`), наглядач живий, наступна спроба вдається. Тест `Callback_SyncThrowOrNullTask_FailsOnlyTheAttempt`, задача 9.
5. Приймач закривають двічі (`using` плюс явний `Dispose`) або не запускають зовсім: 1203 пишеться рівно один раз або жодного разу, а незапущений приймач не читає годинник. Тест `Dispose_Twice_StoppedLoggedOnce_NeverStarted_Silent`, задача 4.

Поза розділом 10 спеки план також додає: `ControlClientLogging_PublishReasons` (задача 1), тести роботи через інтерфейс (задача 2), `ConnectAsync_PassesLoggerFactoryToIdentification` (задача 3), `ReceiveLoop_DoesNotAllocatePerPacket_WithLoggingEnabled` (задача 4), `Dispose_Basic_CompletesUnsolicitedAfterCompletion` (задача 6), `ForeignReply_WithoutRealReply_ClosesAfterDeadline` (задача 7), `TimeProvider_Null_Throws` (задачі 1, 4, 6).

## File Structure

```
NetSdr/
  NetSdr.csproj                          + Polly.Core 8.8.0, Microsoft.Extensions.Logging.Abstractions 10.0.12 (1)
  Control/
    INetSdrControlClient.cs              спільний контракт обох клієнтів (2)
    NetSdrControlClient.cs               інтерфейс (2); логування, internal SendAsync з іменем пункту, TimeProvider (1);
                                         LateReplyObserver, MaxPayloadSize, ThrowIfPayloadTooLarge (5)
    NetSdrControlClient.Log.cs           ControlClientLog 1000-1011, PublishReason (1)
    NetSdrControlClientOptions.cs        LoggerFactory, internal TimeProvider і Supervised (1)
    PendingRequest.cs                    Item і WriteStartedAt для логів (1)
    ResilientControlClientOptions.cs     опції стійкого клієнта і їхня XML-документація (6)
    ResilientControlClient.cs            API, перевірка опцій, шлях команди, Link, Exchange, CommandExecution (6);
                                         гігієна лінії, перейняття (7); захист від повторного входу (9); CommandTimeout (11)
    ResilientControlClient.Supervisor.cs DisposeAsync (6); наглядач, перепідключення, відмова (8); фаза Restore (9); heartbeat (10)
    ResilientControlClient.Log.cs        ResilientClientLog 1100-1112, ReconnectPhase, LateOutcome, LateOwner (6)
    ConnectionRestoredContext.cs         контекст колбеку (6; викликається з задачі 9)
    RestoreSession.cs                    вкладені RestoreSession, RestoreScope, FatalRestoreException (9)
  Data/
    DataPacketInfo.cs                    без Timestamp і UtcTime (4)
    DataReceiverOptions.cs               LoggerFactory, StatisticsLogInterval, internal TimeProvider (4)
    NetSdrDataReceiver.cs                перевірка підсумку раз на 256 датаграм, логування (4)
    NetSdrDataReceiver.Log.cs            DataReceiverLog 1200-1206 (4)
  Identification/
    IdentificationOptions.cs             ProbeAsync через інтерфейс (2), LoggerFactory (3)
    DeviceIdentity.cs, Probes.cs         інтерфейс (2), логування проб і кроків (3)
    DeviceCatalog.cs                     інтерфейс у фабриках і AttachAsync (2), логування (3)
    IdentificationLog.cs                 IdentificationLog 1300-1312 і форматування списків (3)
NetSdr.Tests/
  NetSdr.Tests.csproj                    + TimeProvider.Testing 10.10.0, Diagnostics.Testing 10.10.0 (1)
  FakeLoggerFactory.cs                   ILoggerFactory над FakeLogCollector, помічники записів (1)
  FakeTime.cs                            CountingTimeProvider (4), AdvanceUntilAsync (8)
  LoggingOptionsTests.cs                 null LoggerFactory і TimeProvider для кожного компонента (1, 3, 4, 6)
  EndToEndTests.cs                       + EndToEnd_StreamResumesAfterReconnect (9)
  Control/PipeDevice.cs                  + static Attach(client) (5)
  Control/ForwardingClient.cs            декоратор INetSdrControlClient, що рахує запити (2)
  Control/ControlClientLoggingTests.cs   події простого клієнта (1)
  Control/InnerClientHookTests.cs        internal TimeProvider (1), LateReplyObserver (5)
  Control/Resilient.cs                   Fast/Seam опції, старт сервера, PipeConnector, помічники відповідей (6)
  Control/InterfaceParityTests.cs        обидва клієнти через інтерфейс (6)
  Control/ResilientConnectTests.cs       перше підключення, опції, базове закриття (6)
  Control/ResilientLineTests.cs          гігієна лінії, запізнілі відповіді (7)
  Control/ResilientReconnectTests.cs     наглядач, backoff, відмова, гонки закриття (8, 9)
  Control/ResilientRestoreTests.cs       ConnectionRestored (9)
  Control/ResilientHeartbeatTests.cs     heartbeat і контракт логування стійкого клієнта (10)
  Control/ResilientTimeoutTests.cs       CommandTimeout, скасування, стрес (11)
  Data/PacketCollector.cs                + ArrivalTimestamps (4)
  Data/DataReceiverLoggingTests.cs       підсумки, розриви, помилки обробника (4)
  Identification/DeviceIdentityTests.cs, DeviceCatalogTests.cs   тести через інтерфейс, запис Dev (2)
  Identification/IdentificationLoggingTests.cs                     події 1300-1312 (3)
  Testing/TestServerStreamingTests.cs    темп за ArrivalTimestamps (4)
examples/Vega/
  NetSdr.Examples.Vega/VegaReceiverBase.cs, VegaV1Receiver.cs, VegaV2Receiver.cs   інтерфейс (2), LoggerFactory каталогу (3)
  NetSdr.Examples.Vega.Tests/Receiver/VegaCatalogTests.cs      запис GenericDevice (2)
  NetSdr.Examples.Vega.Tests/Receiver/VegaConnectTests.cs      фабрика логерів доходить до ідентифікації (3)
  NetSdr.Examples.Vega.Tests/Receiver/VegaResilienceTests.cs   каталог над стійким клієнтом (7), обрив під час ідентифікації і обгортка (9)
```

Числа в дужках це задачі. Порядок задач: 1-5 змінюють наявні компоненти, 6-11 будують стійкий клієнт; кожна лишає рішення зібраним і всі тести зеленими.

---

### Task 1: Пакети і логування `NetSdrControlClient`

**Files:**
- Modify: `NetSdr/NetSdr.csproj`, `NetSdr.Tests/NetSdr.Tests.csproj`
- Create: `NetSdr/Control/NetSdrControlClient.Log.cs`
- Modify: `NetSdr/Control/NetSdrControlClientOptions.cs`, `NetSdr/Control/NetSdrControlClient.cs`, `NetSdr/Control/PendingRequest.cs`
- Create: `NetSdr.Tests/FakeLoggerFactory.cs`
- Test: `NetSdr.Tests/Control/ControlClientLoggingTests.cs`, `NetSdr.Tests/Control/InnerClientHookTests.cs`, `NetSdr.Tests/LoggingOptionsTests.cs`

**Interfaces:**
- Produces:
  - `NetSdrControlClientOptions`: `public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;`, `internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;`, `internal bool Supervised { get; set; }`.
  - `NetSdrControlClient`: `internal Task<ControlItemMessage> SendAsync(RequestType type, ushort code, ReadOnlyMemory<byte> payload, string? item, CancellationToken ct)`; публічний `SendAsync` викликає його з `item: null`.
  - `PendingRequest`: `public string Item { get; }` (`typeof(T).Name` для `PendingRequest<T>`, `item ?? "raw"` для `PendingRawRequest`), `public long WriteStartedAt { get; set; }`.
  - `internal enum PublishReason { Unsolicited, Data, LateReply, NoRequest, Foreign, Nak }` і `internal static partial class ControlClientLog` (простір `NetSdr.Control`), кожен метод `public static partial void`, перший параметр `ILogger logger`:
    - 1000 `Connected(LogLevel level, IPEndPoint? remoteEndPoint, IPEndPoint? localEndPoint)`
    - 1001 `Closed(LogLevel level, IPEndPoint? remoteEndPoint)`
    - 1002 `Faulted(LogLevel level, IPEndPoint? remoteEndPoint, Exception exception)`
    - 1003 `RequestSent(RequestType requestType, string item, ushort code, int payloadLength)`
    - 1004 `ReplyReceived(ReplyType replyType, string item, ushort code, TimeSpan duration, int payloadLength)`
    - 1005 `NakReceived(RequestType requestType, string item, ushort code, TimeSpan duration)`
    - 1006 `RequestTimedOut(LogLevel level, RequestType requestType, string item, ushort code, TimeSpan timeout, bool faults)`
    - 1007 `ForeignReply(ReplyType expectedType, ushort code, ReplyType replyType, ushort receivedCode)`
    - 1008 `RequestAbandoned(RequestType requestType, string item, ushort code)`
    - 1009 `MessagePublished(ReplyType replyType, ushort code, int payloadLength, PublishReason reason)`
    - 1010 `FrameSent(string hex)`, 1011 `FrameReceived(string hex)`
  - Тести: `internal sealed class FakeLoggerFactory : ILoggerFactory` з `FakeLoggerFactory(LogLevel minimum = LogLevel.Trace)`, `FakeLogCollector Collector`, `IReadOnlyList<FakeLogRecord> Events(int id)`; розширення `string? Value(this FakeLogRecord r, string key)` і `TimeSpan Span(this FakeLogRecord r, string key)`.

- [ ] **Step 1: Додати пакети**

```bash
dotnet add NetSdr/NetSdr.csproj package Polly.Core --version 8.8.0
dotnet add NetSdr/NetSdr.csproj package Microsoft.Extensions.Logging.Abstractions --version 10.0.12
dotnet add NetSdr.Tests/NetSdr.Tests.csproj package Microsoft.Extensions.TimeProvider.Testing --version 10.10.0
dotnet add NetSdr.Tests/NetSdr.Tests.csproj package Microsoft.Extensions.Diagnostics.Testing --version 10.10.0
dotnet build NetSdr.sln
```

Expected: збірка успішна, 0 помилок.

- [ ] **Step 2: Написати `FakeLoggerFactory`**

```csharp
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace NetSdr.Tests;

/// <summary>Hands out FakeLoggers that share one collector; levels below <c>minimum</c> are disabled and not collected.</summary>
internal sealed class FakeLoggerFactory(LogLevel minimum = LogLevel.Trace) : ILoggerFactory
{
    public FakeLogCollector Collector { get; } =
        FakeLogCollector.Create(new FakeLogCollectorOptions { CollectRecordsForDisabledLogLevels = false });

    public IReadOnlyList<FakeLogRecord> Events(int id) => Collector.GetSnapshot().Where(r => r.Id.Id == id).ToList();

    public ILogger CreateLogger(string categoryName)
    {
        var logger = new FakeLogger(Collector, categoryName);
        for (var level = LogLevel.Trace; level < minimum; level++) logger.ControlLevel(level, false);
        return logger;
    }

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }
}

internal static class FakeLogRecordExtensions
{
    public static string? Value(this FakeLogRecord record, string key) => record.GetStructuredStateValue(key);
    public static TimeSpan Span(this FakeLogRecord record, string key) =>
        TimeSpan.Parse(record.Value(key)!, CultureInfo.InvariantCulture);
}
```

- [ ] **Step 3: Написати тести, що падають**

`NetSdr.Tests/Control/ControlClientLoggingTests.cs` (константи кадрів як у `ControlClientLifecycleTests`):

```csharp
public class ControlClientLoggingTests
{
    const string Category = "NetSdr.Control.NetSdrControlClient";
    const string ProductReply = "08 00 09 00 53 44 52 03";

    static NetSdrControlClientOptions Logged(FakeLoggerFactory logs, bool fault = true, bool supervised = false) => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(150), FaultOnTimeout = fault, LoggerFactory = logs, Supervised = supervised,
    };

    [Fact]
    public async Task ControlClientLogging_LifecycleAndRequests()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        {
            await client.SetAsync(new RfGain(0, -20));
            await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>());
            await client.DisposeAsync();
        }

        var connected = Assert.Single(logs.Events(1000));
        Assert.Equal((LogLevel.Information, Category), (connected.Level, connected.Category));
        Assert.Equal($"Connected to {client.RemoteEndPoint} from {client.LocalEndPoint}", connected.Message);
        Assert.Equal((LogLevel.Debug, "RfGain"), (logs.Events(1003)[0].Level, logs.Events(1003)[0].Value("Item")));
        var reply = Assert.Single(logs.Events(1004));
        Assert.Equal("RfGain", reply.Value("Item"));
        Assert.True(reply.Span("Duration") >= TimeSpan.Zero);
        var nak = Assert.Single(logs.Events(1005));
        Assert.Equal((LogLevel.Debug, "ProductId"), (nak.Level, nak.Value("Item")));
        Assert.Equal(LogLevel.Information, Assert.Single(logs.Events(1001)).Level);
    }

    [Fact]
    public async Task ControlClientLogging_TimeoutAndForeignReply_Warning()
    {
        var logs = new FakeLoggerFactory();
        await using var silent = PipeDevice.Create(Logged(logs, fault: false));
        var timedOut = silent.Client.GetAsync<ProductId>();
        await silent.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut.WaitAsync(Limits.Test));
        Assert.Equal((LogLevel.Warning, "False"), (logs.Events(1006)[0].Level, logs.Events(1006)[0].Value("Faults")));

        await using var foreign = PipeDevice.Create(Logged(logs, fault: false));
        var failed = foreign.Client.GetAsync<InterfaceVersion>();
        await foreign.ReadRequestAsync();
        await foreign.SendAsync(ProductReply);                                     // a reply for another item
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => failed.WaitAsync(Limits.Test));
        Assert.Equal(LogLevel.Warning, Assert.Single(logs.Events(1007)).Level);
        Assert.Contains(logs.Events(1009), r => r.Value("Reason") == "Foreign");
    }

    [Fact]
    public async Task ControlClientLogging_DeviceClose_Error()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await server.DisconnectClientAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        }

        var faulted = Assert.Single(logs.Events(1002));
        Assert.Equal(LogLevel.Error, faulted.Level);
        Assert.IsAssignableFrom<IOException>(faulted.Exception);
        Assert.Empty(logs.Events(1001));
    }

    [Fact]
    public async Task ControlClientLogging_Supervised_DowngradesToDebug()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(ProductId.Code, _ => ControlReply.Silent),
            Logged(logs, fault: false, supervised: true));
        await using (server)
        await using (client)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<ProductId>());   // 1006
            await server.DisconnectClientAsync();                                             // 1002
            await Assert.ThrowsAnyAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        }

        var (other, closed) = await Loopback.StartAsync(options: Logged(logs, supervised: true));
        await using (other) await closed.DisposeAsync();                                      // 1001
        foreach (int id in new[] { 1000, 1001, 1002, 1006 })
        {
            Assert.NotEmpty(logs.Events(id));
            Assert.All(logs.Events(id), r => Assert.Equal(LogLevel.Debug, r.Level));
        }
    }

    [Theory]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Trace, true)]
    public async Task ControlClientLogging_TraceHex(LogLevel minimum, bool hex)
    {
        var logs = new FakeLoggerFactory(minimum);
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await client.SetAsync(new RfGain(0, -20));
        }

        Assert.Equal(hex, logs.Events(1010).Any(r => r.Message == "-> 0600380000EC"));
        Assert.Equal(hex, logs.Events(1011).Any(r => r.Message == "<- 0600380000EC"));
    }

    [Fact]
    public async Task ControlClientLogging_Unsolicited_Debug()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(options: Logged(logs));
        await using (server)
        await using (client)
        {
            await server.SendUnsolicitedAsync(new AfGain(0, 9));
            await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
            await Assert.ThrowsAsync<NetSdrNakException>(
                () => client.SendAsync(RequestType.Get, 0x7FFF, ReadOnlyMemory<byte>.Empty));
        }

        var published = Assert.Single(logs.Events(1009));
        Assert.Equal((LogLevel.Debug, "Unsolicited"), (published.Level, published.Value("Reason")));
        Assert.Equal("raw", logs.Events(1003).Last().Value("Item"));
    }

    [Fact]
    public async Task ControlClientLogging_PublishReasons()
    {
        var logs = new FakeLoggerFactory();
        await using var device = PipeDevice.Create(Logged(logs, fault: false));
        await device.SendAsync("06 20 48 00 00 09");          // Unsolicited AfGain
        await device.SendAsync("06 80 01 02 03 04");          // data item
        await device.SendAsync("06 00 03 00 11 02");          // a response nobody waits for
        await device.SendAsync("02 00");                      // a NAK without a request
        await Eventually.ThatAsync(() => logs.Events(1009).Count == 4);   // processed before a request is in flight
        var late = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => late.WaitAsync(Limits.Test));
        await device.SendAsync(ProductReply);                  // the late reply of the abandoned request
        var foreign = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("05 00 01 00 41");              // Response 0x0001: foreign
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => foreign.WaitAsync(Limits.Test));
        Assert.Equal(new[] { "Unsolicited", "Data", "NoRequest", "Nak", "LateReply", "Foreign" },
            logs.Events(1009).Select(r => r.Value("Reason")));
    }
}
```

`NetSdr.Tests/Control/InnerClientHookTests.cs`:

```csharp
public class InnerClientHookTests
{
    [Fact]
    public async Task InnerTimeProvider_DrivesResponseTimeout()
    {
        var time = new FakeTimeProvider();
        await using var device = PipeDevice.Create(new NetSdrControlClientOptions
        {
            ResponseTimeout = TimeSpan.FromSeconds(2), FaultOnTimeout = false, TimeProvider = time,
        });
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Task.Delay(100);                                 // the wait has started after the write
        time.Advance(TimeSpan.FromSeconds(1.9));
        await Task.Delay(100);
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(0.2));
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
    }
}
```

`NetSdr.Tests/LoggingOptionsTests.cs`; задачі 3, 4 і 6 додають рядки `InlineData` і гілки `switch`:

```csharp
public class LoggingOptionsTests
{
    [Theory]
    [InlineData("NetSdrControlClient")]
    public Task LoggerFactory_Null_Throws(string component) =>
        Assert.ThrowsAsync<ArgumentNullException>(() => CreateAsync(component, nullLoggerFactory: true));

    [Theory]
    [InlineData("NetSdrControlClient")]
    public Task TimeProvider_Null_Throws(string component) =>
        Assert.ThrowsAsync<ArgumentNullException>(() => CreateAsync(component, nullLoggerFactory: false));

    // Exactly one of the two options is null; a synchronous throw is caught by ThrowsAsync like a faulted task.
    static Task CreateAsync(string component, bool nullLoggerFactory) => component switch
    {
        "NetSdrControlClient" => Run(() => new NetSdrControlClient(nullLoggerFactory
            ? new NetSdrControlClientOptions { LoggerFactory = null! }
            : new NetSdrControlClientOptions { TimeProvider = null! })),
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };

    static Task Run(Func<object> create)
    {
        create();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ControlClientLoggingTests|FullyQualifiedName~InnerClientHookTests|FullyQualifiedName~LoggingOptionsTests"`
Expected: збірка падає, `NetSdrControlClientOptions` не містить `LoggerFactory`, `TimeProvider`, `Supervised`.

- [ ] **Step 5: Опції, `PendingRequest`, `ControlClientLog`**

`NetSdrControlClientOptions` отримує три властивості з Interfaces (XML-документація: `Supervised` ставить лише `ResilientControlClient`, спека 3.2). `PendingRequest` отримує `Item` через конструктор і `WriteStartedAt`. `ControlClientLog` у `NetSdrControlClient.Log.cs`: атрибути `[LoggerMessage(EventId = n, EventName = "...", Level = ..., Message = "...")]` дослівно з таблиці спеки 3.2; у 1000, 1001, 1002, 1006 `Level` в атрибуті немає.

- [ ] **Step 6: Логування і `TimeProvider` у `NetSdrControlClient`**

Конструктор: `ArgumentNullException.ThrowIfNull(options.LoggerFactory)` і `ThrowIfNull(options.TimeProvider)` (з `paramName: nameof(options)`), потім `_logger = options.LoggerFactory.CreateLogger(typeof(NetSdrControlClient).FullName!)`, `_timeProvider`, `_supervised`. `AwaitReplyAsync`: `reply.WaitAsync(_responseTimeout, _timeProvider, ct)`. Новий internal `SendAsync` з `item` бере тіло нинішнього публічного; публічний делегує з `item: null`. Точки логування, кожна поза `_sync`:

| Подія | Місце |
|---|---|
| 1000 | `Attach(...)` після замка; рівень `_supervised ? Debug : Information` |
| 1001 | `DisposeAsync`, лише коли попередній стан `Connected` (прочитати під замком, писати після) |
| 1002 | `Fault`, лише виклик, що перевів у `Faulted`; `Error` або `Debug` |
| 1003, 1010 | `SendFrameAsync` після успішного `WriteAsync`; 1010 отримує `Convert.ToHexString(frame.Memory.Span)` (стрілку дає шаблон `-> {Hex}`), рядок будується лише під `IsEnabled(Trace)` |
| 1011 | початок `ProcessFrame`, увесь кадр із заголовком, лише під `IsEnabled(Trace)` |
| 1004 | `HandleReply`, гілка `answers`; `Duration = _timeProvider.GetElapsedTime(pending.WriteStartedAt)` |
| 1005 | `HandleNak` з активним запитом |
| 1006 | `AwaitReplyAsync`, таймаут після успішного `TryAbandon`; `Faults = faultClient`; `Warning` або `Debug` |
| 1007 | `HandleReply`, третя гілка, перед `Publish` |
| 1008 | `AwaitReplyAsync`, скасування після успішного `TryAbandon` |
| 1009 | `Publish(type, code, payload, reason)` перед `TryWrite`; причини за спекою 3.2: `Data` для Data Items і ACK, `Unsolicited`, `Nak` для NAK без активного запиту, `LateReply` для гілки покинутого слота, `NoRequest` для відповіді без запиту і слота, `Foreign` для третьої гілки |

`Register` ставить `pending.WriteStartedAt = _timeProvider.GetTimestamp()` під `_sync` перед поверненням потоку.

- [ ] **Step 7: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ControlClientLoggingTests|FullyQualifiedName~InnerClientHookTests|FullyQualifiedName~LoggingOptionsTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене, `ControlClientLifecycleTests` і `ControlClientProtocolTests` без змін.

- [ ] **Step 8: Commit**

```bash
git add NetSdr/NetSdr.csproj NetSdr.Tests/NetSdr.Tests.csproj NetSdr/Control/NetSdrControlClient.Log.cs NetSdr/Control/NetSdrControlClientOptions.cs NetSdr/Control/NetSdrControlClient.cs NetSdr/Control/PendingRequest.cs NetSdr.Tests/FakeLoggerFactory.cs NetSdr.Tests/Control/ControlClientLoggingTests.cs NetSdr.Tests/Control/InnerClientHookTests.cs NetSdr.Tests/LoggingOptionsTests.cs
git commit -m "feat: log NetSdrControlClient events and drive its response timeout by an internal TimeProvider" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Інтерфейс `INetSdrControlClient` і перехід ідентифікації та Vega

**Files:**
- Create: `NetSdr/Control/INetSdrControlClient.cs`, `NetSdr.Tests/Control/ForwardingClient.cs`
- Modify: `NetSdr/Control/NetSdrControlClient.cs` (оголошення класу)
- Modify: `NetSdr/Identification/IdentificationOptions.cs`, `NetSdr/Identification/DeviceIdentity.cs`, `NetSdr/Identification/Probes.cs`, `NetSdr/Identification/DeviceCatalog.cs`
- Modify: `examples/Vega/NetSdr.Examples.Vega/VegaReceiverBase.cs`, `VegaV1Receiver.cs`, `VegaV2Receiver.cs`
- Modify: `examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaCatalogTests.cs` (запис `GenericDevice`)
- Test: `NetSdr.Tests/Identification/DeviceIdentityTests.cs`, `NetSdr.Tests/Identification/DeviceCatalogTests.cs` (і запис `Dev`)

**Interfaces:**
- Consumes: нічого нового.
- Produces:
  - `public interface INetSdrControlClient : IAsyncDisposable` (простір `NetSdr.Control`) з членами спеки 4.1 і англійською XML-документацією, що передає зміст коментарів спеки 4.1: `SetAsync<T>`, `GetAsync<T>`, `GetAsync<T, TKey>`, `GetRangeAsync<T, TKey>`, `SendAsync(RequestType, ushort, ReadOnlyMemory<byte>, CancellationToken)`, `ChannelReader<ControlItemMessage> Unsolicited`, `Task Completion`, `bool IsConnected`, `IPEndPoint? LocalEndPoint`, `IPEndPoint? RemoteEndPoint`.
  - `public sealed class NetSdrControlClient : INetSdrControlClient`.
  - Сигнатури спеки 4.5: `ProbeAsync(INetSdrControlClient client, ...)`, `DeviceIdentity.ReadAsync(INetSdrControlClient client, ...)`, `Probes.TryGetAsync`, `StandardProbes.RunAsync`, `ReadFirmwareAsync` з `INetSdrControlClient`; `DeviceCatalog<TDevice>.Register`, `Default` і `AttachAsync` з `INetSdrControlClient`; `VegaReceiverBase(INetSdrControlClient control, DeviceIdentity identity)`, `INetSdrControlClient Control { get; }`, `VegaV1Receiver(INetSdrControlClient, DeviceIdentity)`, `VegaV2Receiver(INetSdrControlClient, DeviceIdentity)`.
  - Тести: `internal sealed class ForwardingClient(INetSdrControlClient inner) : INetSdrControlClient` з полем `public int Requests` (інкремент у кожному з п'яти методів запиту, далі пересилання до `inner`; властивості і `DisposeAsync` пересилаються).

- [ ] **Step 1: Написати тести, що падають**

У `DeviceCatalogTests` запис стає `sealed record Dev(string Kind, INetSdrControlClient Client, DeviceIdentity Identity) : IAsyncDisposable`. Нові тести:

```csharp
// DeviceIdentityTests
[Fact]
public async Task ReadAsync_WorksThroughAnyINetSdrControlClient()
{
    var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
    await using (server)
    await using (client)
    {
        var forwarding = new ForwardingClient(client);
        var identity = await DeviceIdentity.ReadAsync(forwarding);
        Assert.Equal(new Version(5, 29), identity.InterfaceVersion);
        Assert.Equal(9, forwarding.Requests);   // six standard items, 0x0004 four times
    }
}

// DeviceCatalogTests
[Fact]
public async Task AttachAsync_HandsTheGivenClientToTheFactory()
{
    var (server, client) = await Loopback.StartAsync();
    await using (server)
    await using (client)
    {
        var forwarding = new ForwardingClient(client);
        var dev = await new DeviceCatalog<Dev>().Default((c, id) => new Dev("any", c, id)).AttachAsync(forwarding);
        Assert.Same(forwarding, dev.Client);
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~DeviceIdentityTests|FullyQualifiedName~DeviceCatalogTests"`
Expected: збірка падає, тип `INetSdrControlClient` не знайдено.

- [ ] **Step 3: Створити інтерфейс і перевести на нього бібліотеку, Vega і тестові записи**

Замінити тип параметрів скрізь, де Interfaces називає сигнатуру. `ConnectAsync` каталогу і Vega не змінюються: вони створюють `NetSdrControlClient`. Посилання `cref` у документації (`ReadEventsAsync` і клас `VegaReceiverBase`) перевести на `INetSdrControlClient`. У `VegaCatalogTests` запис `GenericDevice(INetSdrControlClient Client, DeviceIdentity Identity)`. Жодних перевантажень зі старими типами.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln`
Expected: PASS, включно з усіма тестами Vega.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/INetSdrControlClient.cs NetSdr/Control/NetSdrControlClient.cs NetSdr/Identification/IdentificationOptions.cs NetSdr/Identification/DeviceIdentity.cs NetSdr/Identification/Probes.cs NetSdr/Identification/DeviceCatalog.cs examples/Vega/NetSdr.Examples.Vega/VegaReceiverBase.cs examples/Vega/NetSdr.Examples.Vega/VegaV1Receiver.cs examples/Vega/NetSdr.Examples.Vega/VegaV2Receiver.cs examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaCatalogTests.cs NetSdr.Tests/Control/ForwardingClient.cs NetSdr.Tests/Identification/DeviceIdentityTests.cs NetSdr.Tests/Identification/DeviceCatalogTests.cs
git commit -m "feat: add INetSdrControlClient and move identification, catalog and Vega onto it" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Логування ідентифікації і каталогу

**Files:**
- Create: `NetSdr/Identification/IdentificationLog.cs`
- Modify: `NetSdr/Identification/IdentificationOptions.cs`, `NetSdr/Identification/DeviceIdentity.cs`, `NetSdr/Identification/Probes.cs`, `NetSdr/Identification/DeviceCatalog.cs`
- Modify: `examples/Vega/NetSdr.Examples.Vega/VegaReceiverBase.cs` (`ConnectCoreAsync`)
- Test: `NetSdr.Tests/Identification/IdentificationLoggingTests.cs`, `NetSdr.Tests/LoggingOptionsTests.cs`, `examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaConnectTests.cs`

**Interfaces:**
- Consumes: `INetSdrControlClient` (задача 2), `FakeLoggerFactory` і `Value` (задача 1).
- Produces:
  - `IdentificationOptions.LoggerFactory` (`public ILoggerFactory`, `NullLoggerFactory.Instance`).
  - `internal static partial class IdentificationLog` (простір `NetSdr.Identification`), перший параметр `ILogger logger`:
    - 1300 `ProbeAnswered(string item, ushort code, object? value)`, 1301 `ProbeUnsupported(string item, ushort code)`
    - 1302 `ProbeCompleted(int index, int count, TimeSpan duration, string facts, string unsupported)`
    - 1303 `IdentificationFailed(string step, TimeSpan duration, Exception exception)`
    - 1304 `IdentityRead(string? name, KnownModel model, string? serialNumber, Version? firmwareVersion, uint? productId, TimeSpan duration, string unsupported, string facts)`
    - 1310 `DeviceMatched(string? name, KnownModel model, string registration)`
    - 1311 `DeviceNotRecognized(string? name, KnownModel model, uint? productId, string candidates)`
    - 1312 `AttachFailed(string clientFate, Exception exception)`
    - `internal static string Codes(IEnumerable<ushort> codes)`: за зростанням, `"0x0002, 0x0009"`, порожній список `"none"`; `internal static string Names(IEnumerable<string> names)`: через `", "`, порожній `"none"`.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class IdentificationLoggingTests
{
    static IdentificationOptions Logged(FakeLoggerFactory logs, bool standard = true) =>
        new() { IncludeStandardProbes = standard, LoggerFactory = logs };

    static void PreloadAll(NetSdrTestServer s)
    {
        s.Preload(new TargetName("NetSDR"));
        s.Preload(new SerialNumber("PS000123"));
        s.Preload(new InterfaceVersion(529));
        for (byte id = 0; id <= 3; id++) s.Preload(new FirmwareVersion(id, (ushort)(100 + id)));
        s.Preload(new ProductId(0x12345678));
        s.Preload(new Options(0, 0, 0));
    }

    [Fact]
    public async Task IdentificationLogging_StandardProbes()
    {
        var bare = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            await DeviceIdentity.ReadAsync(client, Logged(bare));
        }

        Assert.Equal(9, bare.Events(1301).Count);
        Assert.Equal(4, bare.Events(1301).Count(r => r.Value("Item")!.StartsWith("FirmwareVersion id ")));
        Assert.Empty(bare.Events(1300));

        var full = new FakeLoggerFactory();
        var (server2, client2) = await Loopback.StartAsync(PreloadAll);
        await using (server2)
        await using (client2)
        {
            await DeviceIdentity.ReadAsync(client2, Logged(full));
        }

        Assert.Equal(9, full.Events(1300).Count);
        Assert.Contains(full.Events(1300), r => r.Message == "Standard probe TargetName 0x0001 answered: NetSDR");
        var read = Assert.Single(full.Events(1304));
        Assert.Equal((LogLevel.Information, "NetSdr.Identification.DeviceIdentity"), (read.Level, read.Category));
        Assert.Equal("none", read.Value("Unsupported"));
    }

    [Fact]
    public async Task IdentificationLogging_AppProbe()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            var options = Logged(logs, standard: false);
            options.Probes.Add(Probes.Item<InterfaceVersion>());
            options.Probes.Add(Probes.Item<ProductId>());   // NAK
            await DeviceIdentity.ReadAsync(client, options);
        }

        var probes = logs.Events(1302);
        Assert.Equal(("1", "2", "InterfaceVersion", "none"),
            (probes[0].Value("Index"), probes[0].Value("Count"), probes[0].Value("Facts"), probes[0].Value("Unsupported")));
        Assert.Equal(("none", "0x0009"), (probes[1].Value("Facts"), probes[1].Value("Unsupported")));
    }

    [Fact]
    public async Task IdentificationLogging_ProbeFails()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync(s => s.OnRequest(TargetName.Code, _ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150) });
        await using (server)
        await using (client)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => DeviceIdentity.ReadAsync(client, Logged(logs)));
        }

        var failed = Assert.Single(logs.Events(1303));
        Assert.Equal(("TargetName", LogLevel.Debug), (failed.Value("Step"), failed.Level));
        Assert.IsType<TimeoutException>(failed.Exception);
    }

    [Fact]
    public async Task CatalogLogging_Matched()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            await new DeviceCatalog<object>(Logged(logs, standard: false)).Register("Any", _ => true, (c, id) => id).AttachAsync(client);
            await new DeviceCatalog<object>(Logged(logs, standard: false)).Default((c, id) => id).AttachAsync(client);
        }

        var matched = logs.Events(1310);
        Assert.Equal(new[] { "Any", "default" }, matched.Select(r => r.Value("Registration")));
        Assert.All(matched, r => Assert.Equal(("NetSdr.Identification.DeviceCatalog", LogLevel.Information), (r.Category, r.Level)));
    }

    [Fact]
    public async Task CatalogLogging_NotRecognized()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Loopback.StartAsync();
        await using (server)
        await using (client)
        {
            var catalog = new DeviceCatalog<object>(Logged(logs, standard: false))
                .Register("A", _ => false, (c, id) => id).Register("B", _ => false, (c, id) => id);
            await Assert.ThrowsAsync<DeviceNotRecognizedException>(() => catalog.AttachAsync(client));
        }

        var warning = Assert.Single(logs.Events(1311));
        Assert.Equal((LogLevel.Warning, "A, B"), (warning.Level, warning.Value("Candidates")));
        Assert.Empty(logs.Events(1312));
    }

    [Fact]
    public async Task CatalogLogging_FactoryThrows()
    {
        var logs = new FakeLoggerFactory();
        var catalog = new DeviceCatalog<object>(Logged(logs, standard: false))
            .Register("Broken", _ => true, (c, id) => throw new InvalidOperationException("boom"));
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        var endPoint = new IPEndPoint(IPAddress.Loopback, server.Port);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ConnectAsync(endPoint));
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(endPoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.AttachAsync(client));
        Assert.Equal(new[] { "the client is closed", "the caller keeps the client" },
            logs.Events(1312).Select(r => r.Value("ClientFate")));
        Assert.All(logs.Events(1312), r => Assert.IsType<InvalidOperationException>(r.Exception));
    }
}
```

У `LoggingOptionsTests.LoggerFactory_Null_Throws` два нові рядки `[InlineData("DeviceCatalog")]` і `[InlineData("DeviceIdentity.ReadAsync")]`, гілки `switch`:

```csharp
"DeviceCatalog" => Run(() => new DeviceCatalog<object>(new IdentificationOptions { LoggerFactory = null! })),
"DeviceIdentity.ReadAsync" => DeviceIdentity.ReadAsync(new NetSdrControlClient(), new IdentificationOptions { LoggerFactory = null! }),
```

У `VegaConnectTests` (лише абстракції логування, що приходять транзитивно через `NetSdr`):

```csharp
[Fact]
public async Task ConnectAsync_PassesLoggerFactoryToIdentification()
{
    var logs = new CategoryRecorder();
    await using var emulator = new VegaEmulator();
    await emulator.StartAsync();
    await using var vega = await VegaReceiverBase.ConnectAsync("127.0.0.1", emulator.Port, VegaEmulator.DefaultKey,
        new NetSdrControlClientOptions { LoggerFactory = logs });
    Assert.Contains("NetSdr.Identification.DeviceIdentity", logs.Categories);
    Assert.Contains("NetSdr.Identification.DeviceCatalog", logs.Categories);
}

sealed class CategoryRecorder : ILoggerFactory
{
    public ConcurrentBag<string> Categories { get; } = [];
    public ILogger CreateLogger(string categoryName) { Categories.Add(categoryName); return NullLogger.Instance; }
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~IdentificationLoggingTests|FullyQualifiedName~LoggingOptionsTests|FullyQualifiedName~VegaConnectTests"`
Expected: збірка падає, `IdentificationOptions` не містить `LoggerFactory`.

- [ ] **Step 3: `IdentificationLog` і логування `DeviceIdentity.ReadAsync`**

Атрибути дослівно зі спеки 3.5. `ReadAsync` лишається `async`, тож `ArgumentNullException.ThrowIfNull(options.LoggerFactory, nameof(options))` летить через задачу; логер `typeof(DeviceIdentity).FullName`, тривалості через `TimeProvider.System`. Увесь виклик у `try`; `catch (Exception ex)` пише 1303 з поточним кроком і тривалістю від початку, потім `throw;`. Кроки: `TargetName`, `SerialNumber`, `InterfaceVersion`, `FirmwareVersion id {n}`, `ProductId`, `Options`, далі `probe {i} of {n}` (i з 1). `StandardProbes.RunAsync` отримує логер і спосіб повідомити крок (форма на вибір виконавця) і пише 1300 або 1301 на кожен запит стандартної проби, з `Item` = крок. `Value` у 1300: значення, записане в builder (`string`, `Version`, `FpgaInfo`, `uint`), для `Options` рядок `flags 0x{Flags:X2}, custom 0x{Custom:X2}, detail 0x{Detail:X8}`, для відповіді 0x0004 без версії `none`. Після кожної проби застосунку 1302: `facts` це `Names` імен типів фактів, яких не було до проби, `unsupported` це `Codes` нових кодів (знімки `Facts.Keys` і `UnsupportedCodes` до і після). Наприкінці 1304 з полями готового паспорта, `Codes(identity.Unsupported)` і `Names` усіх типів фактів.

- [ ] **Step 4: Логування `DeviceCatalog` і Vega**

Конструктор: `var factory = identification?.LoggerFactory ?? NullLoggerFactory.Instance`, `null` у переданих опціях дає `ArgumentNullException`; логер з категорією `"NetSdr.Identification.DeviceCatalog"`. `CreateAsync`: після збігу 1310 з іменем реєстрації або `default`; без збігу і без дефолту 1311 з `Names(Registrations)`, потім `DeviceNotRecognizedException`. Будь-який інший виняток після підключення (ідентифікація, предикат, фабрика, фабрика повернула `null`) пише 1312: `the client is closed` у `ConnectCoreAsync`, `the caller keeps the client` в `AttachAsync`, потім летить далі; для `DeviceNotRecognizedException` 1312 немає. Vega `ConnectCoreAsync`: `new IdentificationOptions { LoggerFactory = options?.LoggerFactory ?? NullLoggerFactory.Instance, Probes = { VegaProbes.Identify(unlockKey) } }`.

- [ ] **Step 5: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~IdentificationLoggingTests|FullyQualifiedName~LoggingOptionsTests|FullyQualifiedName~VegaConnectTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 6: Commit**

```bash
git add NetSdr/Identification/IdentificationLog.cs NetSdr/Identification/IdentificationOptions.cs NetSdr/Identification/DeviceIdentity.cs NetSdr/Identification/Probes.cs NetSdr/Identification/DeviceCatalog.cs examples/Vega/NetSdr.Examples.Vega/VegaReceiverBase.cs NetSdr.Tests/Identification/IdentificationLoggingTests.cs NetSdr.Tests/LoggingOptionsTests.cs examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaConnectTests.cs
git commit -m "feat: log identification probes and catalog decisions" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Приймач даних: `DataPacketInfo` без часу, логування і підсумки

**Files:**
- Modify: `NetSdr/Data/DataPacketInfo.cs`, `NetSdr/Data/DataReceiverOptions.cs`, `NetSdr/Data/NetSdrDataReceiver.cs`
- Create: `NetSdr/Data/NetSdrDataReceiver.Log.cs`, `NetSdr.Tests/FakeTime.cs`
- Modify: `NetSdr.Tests/Data/PacketCollector.cs`, `NetSdr.Tests/Testing/TestServerStreamingTests.cs` (`RealTimePacing_ChannelsShareTheSampleRate`)
- Test: `NetSdr.Tests/Data/DataReceiverLoggingTests.cs`, `NetSdr.Tests/Data/DataReceiverTests.cs`, `NetSdr.Tests/LoggingOptionsTests.cs`

**Interfaces:**
- Consumes: `FakeLoggerFactory` (задача 1).
- Produces:
  - `DataPacketInfo`: `Sequence`, `GapBefore`, `IsCaptureStart`, `Format`; `internal DataPacketInfo(ushort sequence, int gapBefore, SampleFormat format)`.
  - `DataReceiverOptions`: `public ILoggerFactory LoggerFactory`, `public TimeSpan StatisticsLogInterval { get; set; } = TimeSpan.FromSeconds(10)`, `internal TimeProvider TimeProvider { get; set; } = TimeProvider.System`.
  - `internal static partial class DataReceiverLog` (простір `NetSdr.Data`), перший параметр `ILogger logger`: 1200 `ReceiveStarted(IPEndPoint localEndPoint, int receiveBufferBytes)`, 1201 `IntervalSummary(long received, long bytes, TimeSpan elapsed)`, 1202 `IntervalSummaryWithLoss(long received, long bytes, TimeSpan elapsed, long lost, long rejected, long handlerErrors)`, 1203 `ReceiveStopped(IPEndPoint? localEndPoint, TimeSpan elapsed, long received, long bytes, long lost, long rejected, long handlerErrors)`, 1204 `SequenceGap(int gap, ushort sequence)`, 1205 `HandlerFailed(ushort sequence, Exception exception)`, 1206 `ReceiveFailed(IPEndPoint? localEndPoint, Exception exception)`.
  - Тести: `PacketCollector.ArrivalTimestamps` (`public ConcurrentQueue<long>`); `internal sealed class CountingTimeProvider(TimeProvider inner) : TimeProvider` з `IReadOnlyCollection<string?> TimestampReaders` (ім'я потоку кожного виклику `GetTimestamp`), решта членів пересилається до `inner`.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class DataReceiverLoggingTests
{
    const string ReceiveThread = "NetSdr data receiver";

    static void Send(IPEndPoint target, ushort first, int count)
    {
        for (int i = 0; i < count; i++) UdpTestSender.Send(target, UdpTestSender.Datagram((ushort)(first + i), 1028));
    }

    static Task ReceivedAsync(NetSdrDataReceiver receiver, long count) =>
        Eventually.ThatAsync(() => receiver.Statistics.Received == count);

    static void Ignore(in DataPacketInfo info, ReadOnlySpan<byte> samples) { }

    [Fact]
    public async Task Summary_CleanInterval_Debug1201()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        Send(c.EndPoint, 1, 256);
        await ReceivedAsync(c.Receiver, 256);
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 257, 256);
        await Eventually.ThatAsync(() => logs.Events(1201).Count == 1);
        Assert.Equal(("512", LogLevel.Debug), (logs.Events(1201)[0].Value("Received"), logs.Events(1201)[0].Level));
        Assert.Empty(logs.Events(1202));
    }

    [Fact]
    public async Task Summary_LossInInterval_Warning1202()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        Send(c.EndPoint, 1, 100);
        Send(c.EndPoint, 105, 156);                            // 4 lost before 105
        await ReceivedAsync(c.Receiver, 256);
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 261, 256);
        await Eventually.ThatAsync(() => logs.Events(1202).Count == 1);
        var loss = logs.Events(1202)[0];
        Assert.Equal(("4", "512", LogLevel.Warning), (loss.Value("Lost"), loss.Value("Received"), loss.Level));
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 517, 256);
        await Eventually.ThatAsync(() => logs.Events(1201).Count == 1);
        Assert.Equal("256", logs.Events(1201)[0].Value("Received"));
    }

    [Fact]
    public async Task Summary_Rejected_Warning1202()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        Send(c.EndPoint, 1, 200);
        for (int i = 0; i < 56; i++) UdpTestSender.Send(c.EndPoint, UdpTestSender.Datagram(1, 1028, type: 5));
        await Eventually.ThatAsync(() => c.Receiver.Statistics is { Received: 200, Rejected: 56 });
        time.Advance(TimeSpan.FromSeconds(10));
        Send(c.EndPoint, 201, 256);
        await Eventually.ThatAsync(() => logs.Events(1202).Count == 1);
        Assert.Equal("56", logs.Events(1202)[0].Value("Rejected"));
    }

    [Fact]
    public async Task Summary_ClockReadOncePer256Datagrams()
    {
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        var c = new PacketCollector(new DataReceiverOptions { TimeProvider = clock });
        Send(c.EndPoint, 1, 1024);
        await ReceivedAsync(c.Receiver, 1024);
        c.Dispose();                                           // joins the receive thread: every read is done
        Assert.Equal(4, clock.TimestampReaders.Count(n => n == ReceiveThread));
        Assert.Equal(6, clock.TimestampReaders.Count);          // plus one in Start and one in Dispose
    }

    [Fact]
    public async Task Summary_Disabled_NoClockReadsOnReceiveThread()
    {
        var logs = new FakeLoggerFactory();
        var clock = new CountingTimeProvider(new FakeTimeProvider());
        var c = new PacketCollector(new DataReceiverOptions
        {
            LoggerFactory = logs, TimeProvider = clock, StatisticsLogInterval = Timeout.InfiniteTimeSpan,
        });
        Send(c.EndPoint, 1, 1024);
        await ReceivedAsync(c.Receiver, 1024);
        c.Dispose();
        Assert.DoesNotContain(ReceiveThread, clock.TimestampReaders);
        Assert.Empty(logs.Events(1201).Concat(logs.Events(1202)));
    }

    [Fact]
    public async Task HandlerErrors_FirstPerIntervalLogged()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        using var receiver = new NetSdrDataReceiver(
            (in DataPacketInfo _, ReadOnlySpan<byte> _) => throw new InvalidOperationException("handler"),
            new DataReceiverOptions { LoggerFactory = logs, TimeProvider = time });
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();
        Send(receiver.LocalEndPoint, 1, 256);
        await Eventually.ThatAsync(() => receiver.Statistics.HandlerErrors == 256);
        Assert.IsType<InvalidOperationException>(Assert.Single(logs.Events(1205)).Exception);
        time.Advance(TimeSpan.FromSeconds(10));
        Send(receiver.LocalEndPoint, 257, 256);
        await Eventually.ThatAsync(() => logs.Events(1202).Count == 1);
        Assert.Equal("512", logs.Events(1202)[0].Value("HandlerErrors"));
        Send(receiver.LocalEndPoint, 513, 1);                  // the next interval logs its first error again
        await Eventually.ThatAsync(() => logs.Events(1205).Count == 2);
    }

    [Fact]
    public async Task SequenceGap_DebugPerGap()
    {
        var logs = new FakeLoggerFactory();
        using var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        Send(c.EndPoint, 1, 10);
        Send(c.EndPoint, 13, 8);                               // 2 lost
        Send(c.EndPoint, 25, 6);                               // 4 lost
        await ReceivedAsync(c.Receiver, 24);
        Assert.Equal(new[] { ("2", "13"), ("4", "25") }, logs.Events(1204).Select(r => (r.Value("Gap"), r.Value("Sequence"))));
        Assert.All(logs.Events(1204), r => Assert.Equal(LogLevel.Debug, r.Level));
    }

    [Fact]
    public async Task NoPerPacketEntries_EvenAtTrace()
    {
        var logs = new FakeLoggerFactory(LogLevel.Trace);
        var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        Send(c.EndPoint, 1, 1000);
        await ReceivedAsync(c.Receiver, 1000);
        c.Dispose();
        var ids = logs.Collector.GetSnapshot().Select(r => r.Id.Id).ToArray();
        Assert.All(ids, id => Assert.Contains(id, new[] { 1200, 1201, 1202, 1203 }));
        Assert.Equal((1, 1), (ids.Count(id => id == 1200), ids.Count(id => id == 1203)));
    }

    [Fact]
    public async Task StartAndDispose_Information()
    {
        var logs = new FakeLoggerFactory();
        var c = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        var started = Assert.Single(logs.Events(1200));
        Assert.Equal((LogLevel.Information, c.EndPoint.ToString()), (started.Level, started.Value("LocalEndPoint")));
        Send(c.EndPoint, 1, 3);
        await ReceivedAsync(c.Receiver, 3);
        c.Dispose();
        var stopped = Assert.Single(logs.Events(1203));
        Assert.Equal(("3", LogLevel.Information), (stopped.Value("Received"), stopped.Level));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-10_000_000L)]                                 // -1 s
    [InlineData((int.MaxValue + 1L) * TimeSpan.TicksPerMillisecond)]
    public void StatisticsLogInterval_Invalid_Throws(long ticks) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NetSdrDataReceiver(Ignore, new DataReceiverOptions { StatisticsLogInterval = TimeSpan.FromTicks(ticks) }));

    [Fact]
    public void StatisticsLogInterval_Infinite_Accepted() =>
        new NetSdrDataReceiver(Ignore, new DataReceiverOptions { StatisticsLogInterval = Timeout.InfiniteTimeSpan }).Dispose();

    // Review Focus 5.
    [Fact]
    public void Dispose_Twice_StoppedLoggedOnce_NeverStarted_Silent()
    {
        var logs = new FakeLoggerFactory();
        var started = new PacketCollector(new DataReceiverOptions { LoggerFactory = logs });
        started.Dispose();
        started.Dispose();
        Assert.Single(logs.Events(1203));

        var clock = new CountingTimeProvider(TimeProvider.System);
        var idle = new NetSdrDataReceiver(Ignore, new DataReceiverOptions { LoggerFactory = logs, TimeProvider = clock });
        idle.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        idle.Dispose();
        idle.Dispose();
        Assert.Single(logs.Events(1203));
        Assert.Empty(clock.TimestampReaders);
    }
}
```

У `DataReceiverTests` новий `ReceiveLoop_DoesNotAllocatePerPacket_WithLoggingEnabled`: тіло наявного `ReceiveLoop_DoesNotAllocatePerPacket` з `new DataReceiverOptions { LoggerFactory = new FakeLoggerFactory() }` другим аргументом конструктора і тими самими перевірками (через кожні 256 датаграм потік читає `TimeProvider.System`, що нічого не виділяє).

`LoggingOptionsTests`: `[InlineData("NetSdrDataReceiver")]` в обох теоріях, гілка:

```csharp
"NetSdrDataReceiver" => Run(() => new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { }, nullLoggerFactory
    ? new DataReceiverOptions { LoggerFactory = null! }
    : new DataReceiverOptions { TimeProvider = null! })),
```

`RealTimePacing_ChannelsShareTheSampleRate` чекає `c.ArrivalTimestamps.Count >= 10` і рахує `Stopwatch.GetElapsedTime(arrivals[0], arrivals[9])` з `var arrivals = c.ArrivalTimestamps.ToArray()`; межі `300..700` мс без змін.

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~DataReceiverLoggingTests|FullyQualifiedName~DataReceiverTests|FullyQualifiedName~LoggingOptionsTests"`
Expected: збірка падає, `DataReceiverOptions` не містить `LoggerFactory`, `CountingTimeProvider` не існує.

- [ ] **Step 3: Тестові помічники**

`PacketCollector.Collect` кладе `Stopwatch.GetTimestamp()` в `ArrivalTimestamps` перед `Packets.Enqueue`. `CountingTimeProvider` у `NetSdr.Tests/FakeTime.cs` перевизначає `GetTimestamp` (записує `Thread.CurrentThread.Name` у `ConcurrentQueue<string?>`, потім `inner.GetTimestamp()`), `TimestampFrequency`, `GetUtcNow`, `LocalTimeZone`, `CreateTimer`.

- [ ] **Step 4: `DataPacketInfo`, опції і логування приймача**

`DataPacketInfo` втрачає `Timestamp` і `UtcTime`; `ReceiveLoop` більше не викликає `Stopwatch.GetTimestamp` і `DateTime.UtcNow`. Конструктор приймача: `ThrowIfNull` для `LoggerFactory` і `TimeProvider`; `StatisticsLogInterval` або `Timeout.InfiniteTimeSpan`, або `> TimeSpan.Zero` і `TotalMilliseconds <= int.MaxValue`, інакше `ArgumentOutOfRangeException(nameof(options), ...)`; логер `typeof(NetSdrDataReceiver).FullName`. Правила частоти зі спеки 3.6:

- Наприкінці кожної ітерації `ReceiveLoop`, уже після повернення з `Handle` (лічильники оновлено, відкинуту датаграму пораховано, обробник викликано і його виняток пораховано), коли підсумки ввімкнені, потік прийому збільшує `_sinceCheck`; на 256 скидає його і рівно один раз читає `now = _timeProvider.GetTimestamp()`. Тому 512-та датаграма входить у підсумок свого інтервалу (`Received = 512`, `HandlerErrors = 512`), а нову помилку обробника 1205 відкриває лише 513-та. Тривалості завжди через двоаргументний `GetElapsedTime(start, now)`: одноаргументний сам читає годинник і зламав би лічбу.
- Підсумок, коли від `_intervalStart` минуло `>= StatisticsLogInterval`: прирости п'яти лічильників від бази, 1202 якщо виріс `Lost`, `Rejected` або `HandlerErrors`, інакше 1201; потім нова база, `_intervalStart = now`, прапорець помилки обробника скинуто. Поля бази торкає лише потік прийому.
- `Start` читає годинник один раз (`_startedAt` і `_intervalStart`) і пише 1200 з `LocalEndPoint` і `ActualReceiveBufferSize` після замка. `Dispose` під замком запам'ятовує попередній стан; лише той виклик, що перевів `Started` у `Disposed`, після `Join` читає годинник один раз і пише 1203 з підсумками від `Start`.
- 1204 на кожен пакет із `GapBefore > 0`. 1205 у `catch` обробника, лише коли прапорець не стоїть, потім прапорець ставиться. 1206 у `ReceiveLoop` на `SocketException`, коли стан не `Disposed` (`ConnectionReset` і далі пропускається мовчки).

- [ ] **Step 5: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~DataReceiverLoggingTests|FullyQualifiedName~DataReceiverTests|FullyQualifiedName~TestServerStreamingTests|FullyQualifiedName~LoggingOptionsTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 6: Commit**

```bash
git add NetSdr/Data/DataPacketInfo.cs NetSdr/Data/DataReceiverOptions.cs NetSdr/Data/NetSdrDataReceiver.cs NetSdr/Data/NetSdrDataReceiver.Log.cs NetSdr.Tests/FakeTime.cs NetSdr.Tests/Data/PacketCollector.cs NetSdr.Tests/Data/DataReceiverLoggingTests.cs NetSdr.Tests/Data/DataReceiverTests.cs NetSdr.Tests/Testing/TestServerStreamingTests.cs NetSdr.Tests/LoggingOptionsTests.cs
git commit -m "feat: log UDP receive summaries without per-packet clocks or entries" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Внутрішні гачки `NetSdrControlClient` для стійкого клієнта

**Files:**
- Modify: `NetSdr/Control/NetSdrControlClient.cs`, `NetSdr.Tests/Control/PipeDevice.cs`
- Test: `NetSdr.Tests/Control/InnerClientHookTests.cs`

**Interfaces:**
- Consumes: `PublishReason` і `Publish` (задача 1).
- Produces:
  - `internal const int MaxPayloadSize` (нинішнє значення `FrameHeader.MaxEncodableLength - 4`) і `internal static void ThrowIfPayloadTooLarge(int payloadSize, string? paramName)` з нинішнім текстом винятку; `RentFrame` викликає її.
  - `internal Action<ControlItemMessage, bool>? LateReplyObserver { get; set; }`: ставиться до підключення; `bool` це `isNak`.
  - `PipeDevice`: `public static PipeDevice Attach(NetSdrControlClient client, PipeOptions? toClient = null)` приєднує готовий клієнт до свіжої пари pipe; `Create` будує клієнт і викликає `Attach`.

- [ ] **Step 1: Написати тести, що падають**

```csharp
// InnerClientHookTests
const string ProductReply = "08 00 09 00 53 44 52 03";
const string VersionReply = "06 00 03 00 11 02";

static NetSdrControlClient Inner() =>
    new(new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(150), FaultOnTimeout = false });

static async Task AbandonAsync<T>(PipeDevice device) where T : struct, IControlItem<T>
{
    var call = device.Client.GetAsync<T>();
    await device.ReadRequestAsync();
    await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
}

[Fact]
public async Task LateReplyObserver_FiresForLateReplyAndForNakWithSlotSet()
{
    var calls = new ConcurrentQueue<(ControlItemMessage Message, bool IsNak)>();
    var client = Inner();
    client.LateReplyObserver = (m, nak) => calls.Enqueue((m, nak));
    await using var device = PipeDevice.Attach(client);
    await AbandonAsync<ProductId>(device);
    await device.SendAsync(ProductReply);                       // frees the abandoned slot
    await Eventually.ThatAsync(() => calls.Count == 1);
    await AbandonAsync<InterfaceVersion>(device);
    await device.SendAsync("02 00");                            // NAK, nothing in flight, slot set
    await Eventually.ThatAsync(() => calls.Count == 2);
    var (late, nak) = (calls.ElementAt(0), calls.ElementAt(1));
    Assert.Equal((ReplyType.Response, (ushort)0x0009, false), (late.Message.Type, late.Message.Code, late.IsNak));
    Assert.Equal((ReplyType.Response, (ushort)0, 0, true), (nak.Message.Type, nak.Message.Code, nak.Message.Payload.Length, nak.IsNak));
}

[Fact]
public async Task LateReplyObserver_SilentOtherwise()
{
    int calls = 0;
    var client = Inner();
    client.LateReplyObserver = (_, _) => Interlocked.Increment(ref calls);
    await using var device = PipeDevice.Attach(client);
    var normal = client.GetAsync<InterfaceVersion>();
    await device.ReadRequestAsync();
    await device.SendAsync(VersionReply);
    await normal.WaitAsync(Limits.Test);
    await device.SendAsync("06 20 48 00 00 09");               // unsolicited
    await device.SendAsync("02 00");                            // NAK with an empty slot
    for (int i = 0; i < 2; i++) await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
    var rejected = client.GetAsync<ProductId>();
    await device.ReadRequestAsync();
    await device.SendAsync("02 00");                            // NAK that answers the request
    await Assert.ThrowsAsync<NetSdrNakException>(() => rejected.WaitAsync(Limits.Test));
    Assert.Equal(0, calls);
}

[Fact]
public async Task LateReplyObserver_RunsBeforeUnsolicited()
{
    bool? visibleAtCall = null;
    var client = Inner();
    client.LateReplyObserver = (_, _) => visibleAtCall = client.Unsolicited.TryPeek(out _);
    await using var device = PipeDevice.Attach(client);
    await AbandonAsync<ProductId>(device);
    await device.SendAsync(ProductReply);
    Assert.Equal((ushort)0x0009, (await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code);
    Assert.False(visibleAtCall);
}

[Fact]
public async Task LateReplyObserver_Unset_SameBytes() =>
    Assert.Equal(await ScriptAsync(observe: false), await ScriptAsync(observe: true));

// A late reply, a NAK and an unsolicited frame, then the next request: every frame and message as text.
static async Task<string[]> ScriptAsync(bool observe)
{
    var client = Inner();
    if (observe) client.LateReplyObserver = (_, _) => { };
    await using var device = PipeDevice.Attach(client);
    await AbandonAsync<ProductId>(device);
    await device.SendAsync(ProductReply + " 02 00 06 20 48 00 00 09");
    var seen = new List<string>();
    for (int i = 0; i < 3; i++)   // all three handled before the next request exists, so the NAK cannot reject it
    {
        var m = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        seen.Add($"{m.Type} {m.Code:X4} {Convert.ToHexString(m.Payload.Span)}");
    }

    var next = client.GetAsync<InterfaceVersion>();
    seen.Add(Convert.ToHexString(await device.ReadRequestAsync()));
    await device.SendAsync(VersionReply);
    seen.Add((await next.WaitAsync(Limits.Test)).Version.ToString());
    return seen.ToArray();
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~InnerClientHookTests"`
Expected: збірка падає, немає `PipeDevice.Attach` і `LateReplyObserver`.

- [ ] **Step 3: Реалізувати гачки**

`MaxPayloadSize` і `ThrowIfPayloadTooLarge` як в Interfaces. Спостерігач за спекою 4.2: у `HandleReply`, гілка покинутого слота, і в `HandleNak` типу `Response` без активного запиту, коли `_abandoned` був не `null` (прочитати до очищення), ставиться локальний прапорець під `_sync`; після замка будується одне `ControlItemMessage` (у `HandleNak` це `(Response, 0, empty)`), викликається `LateReplyObserver?.Invoke(message, isNak)`, і те саме повідомлення йде в `Publish` (перевантаження, що приймає готове повідомлення і `PublishReason`). Без спостерігача поведінка побайтово та сама.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~InnerClientHookTests|FullyQualifiedName~ControlClient"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/NetSdrControlClient.cs NetSdr.Tests/Control/PipeDevice.cs NetSdr.Tests/Control/InnerClientHookTests.cs
git commit -m "feat: add the late-reply observer and shared payload check to NetSdrControlClient" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: `ResilientControlClient`: опції, підключення, шлях команди, закриття

Задача будує стійкий клієнт без наглядача: одна спроба підключення з перевіркою, команди через Wire і pipeline повторів, `Unsolicited`, `DisposeAsync`. Втрату з'єднання ще ніхто не обробляє (задача 8), а запит без відповіді ще звільняє лінію (задача 7); тести цієї задачі обох випадків не торкаються.

**Files:**
- Create: `NetSdr/Control/ResilientControlClientOptions.cs`, `NetSdr/Control/ConnectionRestoredContext.cs`, `NetSdr/Control/ResilientControlClient.cs`, `NetSdr/Control/ResilientControlClient.Supervisor.cs`, `NetSdr/Control/ResilientControlClient.Log.cs`
- Create: `NetSdr.Tests/Control/Resilient.cs`
- Test: `NetSdr.Tests/Control/InterfaceParityTests.cs`, `NetSdr.Tests/Control/ResilientConnectTests.cs`, `NetSdr.Tests/LoggingOptionsTests.cs`

**Interfaces:**
- Consumes: `INetSdrControlClient` (2); `NetSdrControlClientOptions.LoggerFactory`, `TimeProvider`, `Supervised` і internal `SendAsync(type, code, payload, item, ct)` (1); `MaxPayloadSize`, `ThrowIfPayloadTooLarge`, `PipeDevice.Attach` (5); `FakeLoggerFactory` (1).
- Produces:
  - Публічне API дослівно зі спеки 5.1 і 5.2: `public sealed partial class ResilientControlClient : INetSdrControlClient` з двома публічними `ConnectAsync` і internal шовом `ConnectAsync(Func<NetSdrControlClient, CancellationToken, Task> connect, string target, ResilientControlClientOptions? options, CancellationToken ct)`; `public sealed class ResilientControlClientOptions` з дев'ятьма публічними властивостями і internal `TimeProvider`, `UseJitter`.
  - `public sealed class ConnectionRestoredContext` дослівно зі спеки 5.3 (`INetSdrControlClient Client`, `Exception Cause`, `DateTimeOffset LostAt`, англійська XML-документація, що передає зміст коментарів спеки 5.3) з `internal ConnectionRestoredContext(INetSdrControlClient client, Exception cause, DateTimeOffset lostAt)`. Його потребує тип `ConnectionRestored`; викликає колбек задача 9.
  - `internal enum ReconnectPhase { Connect, Verify, Restore }`, `internal enum LateOutcome { Reply, Nak }`, `internal enum LateOwner { Heartbeat, CancelledCaller }` у `ResilientControlClient.Log.cs`.
  - `internal static partial class ResilientClientLog`, перший параметр `ILogger logger`, атрибути дослівно зі спеки 3.3:
    - 1100 `CommandRetrying(RequestType requestType, string item, ushort code, int attempt, string reason, Exception exception)`
    - 1101 `HeartbeatMissed(TimeSpan responseTimeout, TimeSpan lateReplyTimeout)`
    - 1102 `ConnectionUnresponsive(RequestType requestType, ushort code, TimeSpan elapsed, IPEndPoint? remoteEndPoint)`
    - 1103 `ConnectionLost(IPEndPoint? remoteEndPoint, Exception cause)`
    - 1104 `ReconnectAttemptFailed(int attempt, string target, ReconnectPhase phase, TimeSpan delay, Exception exception)`
    - 1105 `Reconnected(IPEndPoint? remoteEndPoint, IPEndPoint? localEndPoint, int attempts, TimeSpan downtime)`
    - 1106 `ReconnectGaveUp(string target, int attempts, string reason, Exception exception)`
    - 1107 `LateReplyAdopted(RequestType requestType, string item, ushort code, LateOutcome outcome, TimeSpan late)`
    - 1108 `LateReplyDrained(LateOutcome outcome, RequestType requestType, ushort code, LateOwner owner)`
    - 1109 `Connected(IPEndPoint? remoteEndPoint, IPEndPoint? localEndPoint)`, 1110 `RestoreStarted(IPEndPoint? localEndPoint)`, 1111 `RestoreCompleted(TimeSpan duration)`, 1112 `Disposed(string target)`
  - Приватний каркас, який розширюють задачі 7-11 (усі типи вкладені й приватні, імена зі спеки 6.3-6.5):
    - `enum ClientState { Connected, Reconnecting, Closed }`; поля `volatile ClientState _state`, `volatile Link _link`, `Link? _restoring`, `TaskCompletionSource _changed`, `IOException? _failure`, `bool _disposed`, `Exception? _lastLoss`, `long _lastAttemptStart`; усі під `Lock _sync`.
    - `sealed class Link(NetSdrControlClient client)`: `Client`, `SemaphoreSlim Wire = new(1, 1)`, `Exchange? Current`, `long LastHeard`, `Exception? LossCause`, `Task Pump`, `void Heard(TimeProvider tp)`.
    - `sealed class Exchange`: `Link`, `Type`, `Code`, `Item`, `long SentAt`, `Task<ControlItemMessage> Request`, `TaskCompletionSource<Resolution> Late` (`RunContinuationsAsynchronously`), `ExchangeState State`, `ITimer? Deadline`; `enum ExchangeState { InFlight, Unanswered, Resolved }`; `enum Outcome { Answered, Reply, Nak, Lost }`; `readonly record struct Resolution(Outcome Outcome, ControlItemMessage Message = default, Exception? Cause = null)`.
    - `sealed class CommandExecution`: `Owner`, `Type`, `Code`, `Payload`, `Item`, `Link? Bound` (лише сесія колбеку), `Exchange? Outstanding`, `Exception? LastError`; `static readonly ResiliencePropertyKey<CommandExecution> ExecKey = new("NetSdr.Command")`.
    - Методи: `Task<ControlItemMessage> RunAsync(CommandExecution exec, CancellationToken ct)` (кроки 1-8 спеки 6.5), `ValueTask<ControlItemMessage> AttemptAsync(ResilienceContext context, CommandExecution exec)` (крок 4), `ValueTask<Link> WaitForLinkAsync(CommandExecution exec, CancellationToken t)`, `Exchange StartExchange(Link link, RequestType type, ushort code, ReadOnlyMemory<byte> payload, string? item)` (викликач тримає Wire), `void Settle(Exchange e)`, `bool Resolve(Exchange e, Resolution resolution)` (`true` лише для виклику, що розв'язав; Wire і `Late` поза замком), `Task<Link> OpenLinkAsync(Action<ReconnectPhase>? phase, CancellationToken ct)` (кроки 2-5 спеки 7.4), `Task VerifyAsync(Link link, CancellationToken ct)`, `Task PumpAsync(Link link)`, `static bool ShouldRetryCommand(ResilienceContext context, Exception? exception)`, `Exception ClosedException()` (`ObjectDisposedException` або `_failure`).
  - Тести (`NetSdr.Tests/Control/Resilient.cs`): `static class Resilient` з `Fast`, `Seam`, `StartAsync`, `WithoutStatus`, `FirstTimes`, `Once`, `DropOnce`, `Refused`, `NakEverythingAsync` і константами кадрів; `sealed class PipeConnector` з `Attempts`, `AttemptTimes`, `Before`, `Serve`, `ConnectAsync`, `NextAsync`, `StartAsync`.

- [ ] **Step 1: Написати тестові помічники**

```csharp
/// <summary>Options, servers and reply helpers shared by the ResilientControlClient tests.</summary>
internal static class Resilient
{
    public const string GetStatus = "04 20 05 00";               // the verification and the heartbeat
    public const string Nak = "02 00";
    public const string ProductReply = "08 00 09 00 53 44 52 03";
    public const string VersionReply = "06 00 03 00 11 02";

    /// <summary>Short real timeouts, as ControlClientLifecycleTests.Fast(): a request's deadline is 750 ms after its write.</summary>
    public static ResilientControlClientOptions Fast(FakeLoggerFactory? logs = null) => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(150),
        LateReplyTimeout = TimeSpan.FromMilliseconds(600),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        LoggerFactory = logs is null ? NullLoggerFactory.Instance : logs,
        UseJitter = false,
    };

    /// <summary>For PipeConnector tests: no heartbeat frames on the pipe, optionally fake time.</summary>
    public static ResilientControlClientOptions Seam(FakeLoggerFactory? logs = null, TimeProvider? time = null)
    {
        var options = Fast(logs);
        options.HeartbeatInterval = Timeout.InfiniteTimeSpan;
        options.TimeProvider = time ?? TimeProvider.System;
        return options;
    }

    /// <summary>Starts a test server and connects to it; on failure the server is disposed.</summary>
    public static async Task<(NetSdrTestServer Server, ResilientControlClient Client)> StartAsync(
        ResilientControlClientOptions options, Action<NetSdrTestServer>? setup = null)
    {
        var server = new NetSdrTestServer();
        try
        {
            setup?.Invoke(server);
            await server.StartAsync();
            using var timeout = new CancellationTokenSource(Limits.Test);
            return (server, await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), options, timeout.Token));
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public static IEnumerable<ControlRequest> WithoutStatus(this IEnumerable<ControlRequest> requests) =>
        requests.Where(r => r.Code != StatusCodes.Code);

    /// <summary>A handler that answers the first <paramref name="times"/> requests with <paramref name="first"/>, later ones with <paramref name="then"/> or Echo.</summary>
    public static Func<ControlRequest, ControlReply> FirstTimes(int times, ControlReply first, ControlReply? then = null)
    {
        int seen = 0;
        return _ => Interlocked.Increment(ref seen) <= times ? first : then ?? ControlReply.Echo;
    }

    public static Func<ControlRequest, ControlReply> Once(ControlReply first, ControlReply? then = null) => FirstTimes(1, first, then);

    /// <summary>A handler that drops the connection on the first request and answers later ones with <paramref name="then"/> or Echo.</summary>
    public static Func<ControlRequest, ControlReply> DropOnce(NetSdrTestServer server, ControlReply? then = null)
    {
        int seen = 0;
        return _ =>
        {
            if (Interlocked.Increment(ref seen) > 1) return then ?? ControlReply.Echo;
            _ = server.DisconnectClientAsync();
            return ControlReply.Silent;
        };
    }

    public static Task Refused() => Task.FromException(new SocketException((int)SocketError.ConnectionRefused));

    /// <summary>Answers every request on the pipe with a NAK until the pipe ends or stays silent for Limits.Test.</summary>
    public static async Task NakEverythingAsync(this PipeDevice device)
    {
        try
        {
            while (true)
            {
                await device.ReadRequestAsync();
                await device.SendAsync(Nak);
            }
        }
        catch (Exception)
        {
            // The pipe is gone; the device has nothing left to answer.
        }
    }
}

/// <summary>The connect seam: each new inner client is attached to a fresh PipeDevice that the test drives.</summary>
internal sealed class PipeConnector(TimeProvider? time = null)
{
    private readonly Channel<PipeDevice> _devices = Channel.CreateUnbounded<PipeDevice>();
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>The (fake) time at the start of every attempt, the first ConnectAsync included.</summary>
    public ConcurrentQueue<DateTimeOffset> AttemptTimes { get; } = new();

    /// <summary>Runs first in attempt n (1 is ConnectAsync itself): a faulted task fails the attempt, a pending one delays it.</summary>
    public Func<int, CancellationToken, Task>? Before { get; init; }

    /// <summary>Started in the background for every attached device, for example NakEverythingAsync.</summary>
    public Func<PipeDevice, Task>? Serve { get; init; }

    public async Task ConnectAsync(NetSdrControlClient inner, CancellationToken ct)
    {
        int attempt = Interlocked.Increment(ref _attempts);
        AttemptTimes.Enqueue((time ?? TimeProvider.System).GetUtcNow());
        if (Before is { } before) await before(attempt, ct);
        var device = PipeDevice.Attach(inner);
        if (Serve is { } serve) _ = Task.Run(() => serve(device));
        _devices.Writer.TryWrite(device);
    }

    public async Task<PipeDevice> NextAsync() => await _devices.Reader.ReadAsync().AsTask().WaitAsync(Limits.Test);

    /// <summary>Connects through the seam; without <see cref="Serve"/> the first verification is answered here with a NAK.</summary>
    public async Task<(ResilientControlClient Client, PipeDevice Device)> StartAsync(ResilientControlClientOptions options)
    {
        var connecting = ResilientControlClient.ConnectAsync(ConnectAsync, "pipe", options, CancellationToken.None);
        var device = await NextAsync();
        if (Serve is null)
        {
            Assert.Equal(Hex.Parse(Resilient.GetStatus), await device.ReadRequestAsync());
            await device.SendAsync(Resilient.Nak);
        }

        return (await connecting.WaitAsync(Limits.Test), device);
    }
}
```

- [ ] **Step 2: Написати тести, що падають**

```csharp
public class InterfaceParityTests
{
    static async Task<(NetSdrTestServer Server, INetSdrControlClient Client)> StartAsync(bool resilient, Action<NetSdrTestServer>? setup = null)
    {
        if (resilient)
        {
            var (s, c) = await Resilient.StartAsync(Resilient.Fast(), setup);
            return (s, c);
        }

        var (server, client) = await Loopback.StartAsync(setup);
        return (server, client);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterfaceParity_Commands(bool resilient)
    {
        var (server, client) = await StartAsync(resilient, s =>
        {
            s.Preload(new InterfaceVersion(529));
            s.Preload(new FirmwareVersion(1, 120));
            s.OnRequest(RfGain.Code, r => r.Type == RequestType.GetRange ? ControlReply.Bytes(new byte[] { 0, 0xEC }) : ControlReply.Echo);
        });
        await using (server)
        await using (client)
        {
            Assert.Equal(-20, (await client.SetAsync(new RfGain(0, -20))).GainDb);
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
            Assert.Equal(120, (await client.GetAsync<FirmwareVersion, byte>(1)).Version);
            Assert.Equal(-20, (await client.GetRangeAsync<RfGain, byte>(0)).GainDb);
            var raw = await client.SendAsync(RequestType.Get, InterfaceVersion.Code, ReadOnlyMemory<byte>.Empty);
            Assert.Equal((ReplyType.Response, "1102"), (raw.Type, Convert.ToHexString(raw.Payload.Span)));
            Assert.Equal(ProductId.Code, (await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>())).Code);
            await server.SendUnsolicitedAsync(new AfGain(0, 9));
            ControlItemMessage pushed;
            do pushed = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
            while (pushed.Type != ReplyType.Unsolicited);
            Assert.Equal(AfGain.Code, pushed.Code);
            Assert.True(client.IsConnected);
            Assert.Equal(server.Port, client.RemoteEndPoint!.Port);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterfaceParity_InvalidArguments_ThrowSynchronously_NothingSent(bool resilient)
    {
        var (server, client) = await StartAsync(resilient);
        await using (server)
        await using (client)
        {
            var oversize = Assert.Throws<ArgumentOutOfRangeException>(
                () => { _ = client.SendAsync(RequestType.Set, 0x7000, new byte[NetSdrControlClient.MaxPayloadSize + 1]); });
            Assert.Contains($"the limit is {NetSdrControlClient.MaxPayloadSize} bytes", oversize.Message);
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = client.SendAsync(RequestType.Data0, 0x7000, ReadOnlyMemory<byte>.Empty); });
            Assert.Throws<ArgumentException>(() => { _ = client.SetAsync(new StatusCodes(new[] { StatusCodes.Idle })); });
            await Task.Delay(100);
            Assert.DoesNotContain(server.Received, r => r.Code == 0x7000 || r.Type == RequestType.Set);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterfaceParity_IdentificationAndCatalog(bool resilient)
    {
        var (server, client) = await StartAsync(resilient, s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            var identity = await DeviceIdentity.ReadAsync(client);
            Assert.Equal(new Version(5, 29), identity.InterfaceVersion);
            Assert.Equal(5, identity.Unsupported.Count);
            var device = await new DeviceCatalog<DeviceIdentity>().Default((c, id) => id).AttachAsync(client);
            Assert.Equal(identity.InterfaceVersion, device.InterfaceVersion);
        }
    }
}
```

```csharp
public class ResilientConnectTests
{
    static readonly Dictionary<string, Action<ResilientControlClientOptions>> Invalid = new()
    {
        ["ResponseTimeout 0"] = o => o.ResponseTimeout = TimeSpan.Zero,
        ["ResponseTimeout Infinite"] = o => o.ResponseTimeout = Timeout.InfiniteTimeSpan,
        ["LateReplyTimeout 0"] = o => o.LateReplyTimeout = TimeSpan.Zero,
        ["Sum above Int32"] = o => (o.ResponseTimeout, o.LateReplyTimeout) = (TimeSpan.FromMilliseconds(int.MaxValue), TimeSpan.FromMilliseconds(1)),
        ["CommandTimeout 0"] = o => o.CommandTimeout = TimeSpan.Zero,
        ["HeartbeatInterval -1 s"] = o => o.HeartbeatInterval = TimeSpan.FromSeconds(-1),
        ["ConnectTimeout 0"] = o => o.ConnectTimeout = TimeSpan.Zero,
        ["ReconnectAttempts 0"] = o => o.ReconnectAttempts = 0,
        ["UnsolicitedCapacity 0"] = o => o.UnsolicitedCapacity = 0,
    };

    public static TheoryData<string> InvalidNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (string name in Invalid.Keys) names.Add(name);
            return names;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void Options_Invalid(string name)
    {
        var options = new ResilientControlClientOptions();
        Invalid[name](options);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), options); });
    }

    [Fact]
    public void Options_Defaults()
    {
        var o = new ResilientControlClientOptions();
        Assert.Equal((2, 13, 30, 5, 5), ((int)o.ResponseTimeout.TotalSeconds, (int)o.LateReplyTimeout.TotalSeconds,
            (int)o.CommandTimeout.TotalSeconds, (int)o.HeartbeatInterval.TotalSeconds, (int)o.ConnectTimeout.TotalSeconds));
        Assert.Equal((int.MaxValue, 256, true), (o.ReconnectAttempts, o.UnsolicitedCapacity, o.UseJitter));
        Assert.Same(NullLoggerFactory.Instance, o.LoggerFactory);
    }

    [Fact]
    public async Task FirstConnect()
    {
        // Refused: thrown, nothing keeps running, nothing logged as connected.
        var logs = new FakeLoggerFactory();
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThrowsAsync<SocketException>(() => ResilientControlClient.ConnectAsync(refused.ConnectAsync, "pipe", Resilient.Seam(logs), default));
        await Task.Delay(1200);                                  // longer than the 1 s floor of a reconnect attempt
        Assert.Equal(1, refused.Attempts);
        Assert.Empty(logs.Events(1109));

        // ConnectTimeout, and the caller's token.
        var hanging = new PipeConnector { Before = (_, ct) => Task.Delay(Timeout.Infinite, ct) };
        var slow = Resilient.Seam();
        slow.ConnectTimeout = TimeSpan.FromMilliseconds(200);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => ResilientControlClient.ConnectAsync(hanging.ConnectAsync, "pipe", slow, default));
        Assert.StartsWith("No TCP connection to pipe within", timeout.Message);
        using var cancel = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ResilientControlClient.ConnectAsync(hanging.ConnectAsync, "pipe", Resilient.Seam(), cancel.Token));

        // A device that accepts but never answers the verification.
        var mute = new PipeConnector { Serve = _ => Task.CompletedTask };
        await Assert.ThrowsAsync<TimeoutException>(() => ResilientControlClient.ConnectAsync(mute.ConnectAsync, "pipe", Resilient.Seam(), default));
        Assert.False((await mute.NextAsync()).Client.IsConnected);

        // Success: the bare server NAKs the verification, 1109 is written, ConnectionRestored is not called.
        int callbacks = 0;
        var ok = new FakeLoggerFactory();
        var options = Resilient.Fast(ok);
        options.ConnectionRestored = (_, _) => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; };
        var (server, client) = await Resilient.StartAsync(options);
        await using (server)
        await using (client)
        {
            Assert.True(client.IsConnected);
            Assert.Equal(StatusCodes.Code, server.Received[0].Code);
        }

        var connected = Assert.Single(ok.Events(1109));
        Assert.Equal((LogLevel.Information, "NetSdr.Control.ResilientControlClient"), (connected.Level, connected.Category));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task Dispose_Basic_CompletesUnsolicitedAfterCompletion()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        {
            var completedFirst = client.Unsolicited.Completion.ContinueWith(_ => client.Completion.IsCompletedSuccessfully);
            await client.DisposeAsync();
            await client.DisposeAsync();
            Assert.True(client.Completion.IsCompletedSuccessfully);
            Assert.True(await completedFirst.WaitAsync(Limits.Test));
            Assert.False(client.IsConnected);
            Assert.Throws<ObjectDisposedException>(() => { _ = client.GetAsync<InterfaceVersion>(); });
            await Loopback.AssertServerFreeAsync(server);
        }

        Assert.Equal(LogLevel.Information, Assert.Single(logs.Events(1112)).Level);
    }

    [Fact]
    public async Task ResilientLogging_NullLoggerFactory_Works()
    {
        var (server, client) = await Resilient.StartAsync(new ResilientControlClientOptions(), s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
        }
    }
}
```

`LoggingOptionsTests`: `[InlineData("ResilientControlClient")]` в обох теоріях, гілка:

```csharp
"ResilientControlClient" => Run(() => ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), nullLoggerFactory
    ? new ResilientControlClientOptions { LoggerFactory = null! }
    : new ResilientControlClientOptions { TimeProvider = null! })),
```

- [ ] **Step 3: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~InterfaceParityTests|FullyQualifiedName~ResilientConnectTests|FullyQualifiedName~LoggingOptionsTests"`
Expected: збірка падає, `ResilientControlClient` і `ResilientControlClientOptions` не існують.

- [ ] **Step 4: Опції, контекст колбеку і журнал подій**

`ConnectionRestoredContext` як в Interfaces. `ResilientControlClientOptions` за спекою 5.2 із типовими значеннями з Global Constraints. XML-документація `ConnectionRestored` перелічує правила спеки 5.2 і 7.5: виконується після кожного перепідключення (не після `ConnectAsync`) на перевіреному з'єднанні до будь-якої команди застосунку; запити йдуть через `context.Client`; виклик `ResilientControlClient` з колбеку змушує клієнт відмовитися; будь-який інший виняток, зокрема NAK, провалює лише спробу; токен скасовує `DisposeAsync`; замок застосунку, який тримає потік, що чекає на команду, і який потрібен колбеку, блокує обох до `CommandTimeout`. `ResilientControlClient.Log.cs` містить три enum і `ResilientClientLog` з Interfaces.

- [ ] **Step 5: Підключення і перевірка опцій**

Усі три `ConnectAsync` не `async`: синхронно перевіряють аргументи і опції, копіюють їх у новий екземпляр опцій, потім повертають задачу внутрішнього `ConnectCoreAsync`. Межі зі спеки 5.2: `ResponseTimeout` і `LateReplyTimeout` додатні, скінченні, не більші за `int.MaxValue` мс, і їхня сума теж; `CommandTimeout`, `HeartbeatInterval`, `ConnectTimeout` додатні в тих самих межах або `Timeout.InfiniteTimeSpan`; `ReconnectAttempts >= 1`; `UnsolicitedCapacity >= 1`, інакше `ArgumentOutOfRangeException(nameof(options))`. `LoggerFactory` або `TimeProvider` `null` дає `ArgumentNullException`. `connect` для рядка `(inner, t) => inner.ConnectAsync(host, port, t)` з `target = $"{host}:{port}"`, для `IPEndPoint` `(inner, t) => inner.ConnectAsync(endPoint, t)` з `target = endPoint.ToString()`. Конструктор будує `_innerOptions` дослівно як у спеці 6.7, логер `typeof(ResilientControlClient).FullName`, канал `Unsolicited` (спека 5.4) і pipeline команд дослівно зі спеки 8 (`new ResiliencePipelineBuilder { TimeProvider = options.TimeProvider }`, `OnRetry` пише 1100 з `Reason` `NoReplyWaitingForLateReply` для `TimeoutException` і `ConnectionLost` для `IOException`).

`OpenLinkAsync` (спека 7.4, кроки 2-5 і 9): новий `NetSdrControlClient(_innerOptions)`, `new Link(inner)`, фаза `Connect`: `new CancellationTokenSource(ConnectTimeout, tp)` (не для `Infinite`), зв'язаний з `ct`, звільняється наприкінці; `OperationCanceledException` від таймера стає `TimeoutException($"No TCP connection to {target} within {ConnectTimeout}.")`, від `ct` летить як є. Потім `link.Pump = PumpAsync(link)` (спека 6.8, `link.Heard` на кожне повідомлення), фаза `Verify`: `VerifyAsync` бере Wire, запускає обмін `Get 0x0005` з `item: nameof(StatusCodes)` і чекає його; Response або `NetSdrNakException` проходять, решта летить. На будь-який виняток: `await inner.DisposeAsync()`, `await link.Pump`, `throw`. `ConnectCoreAsync`: `_lastAttemptStart = tp.GetTimestamp()`, `OpenLinkAsync(null, ct)`, потім під `_sync` `_link = link`, `_state = Connected`, і поза замком 1109.

- [ ] **Step 6: Шлях команди**

Кожен публічний метод команди виконує синхронну частину спеки 6.5 крок 0 (без захисту від повторного входу, його додає задача 9): Closed після `DisposeAsync` дає `ObjectDisposedException`, після відмови `InvalidOperationException("The client gave up reconnecting; create a new client.", _failure)`; кодування в новий обнулений `byte[]` з `NetSdrControlClient.ThrowIfPayloadTooLarge` (той самий текст, що в простому клієнті), `T.Write`, `MemoryMarshal.AsBytes(key)`; `SendAsync` перевіряє тип, потім розмір; уже скасований токен дає скасовану задачу. Ім'я пункту `typeof(T).Name` для типізованих викликів, `null` для `SendAsync`. Потім `RunAsync`: крок 1 без дедлайну (його додає задача 11), тобто `token = linked(ct, _lifetime.Token)`; крок 2 admission; крок 3 pipeline зі статичною лямбдою; крок 4 без 4a (перейняття додає задача 7): b `WaitForLinkAsync`, c Wire з перевіркою `IsConnected`, d `StartExchange` під `_sync` і `link.Client.SendAsync(type, code, payload, item, CancellationToken.None)` з продовженням `Settle`, e очікування з `t`; кроки 5-8 за спекою, але крок 7 поки перекладає лише `ct` (як є) і `_lifetime` (`ClosedException()`). Типізований результат читає `ControlItemMessage.ReadItem<T>`. `Settle` у цій задачі: Response або NAK дають `Heard` і `Resolve(Answered)`, будь-що інше `Resolve(Lost(exception))`; задача 7 замінює цю другу гілку. `Resolve` ставить стан під `_sync`, а `Late` завершує і Wire звільняє поза замком. Між захопленням Wire і `StartExchange` жодного виклику логера.

- [ ] **Step 7: `Unsolicited`, `Completion`, `IsConnected`, кінці з'єднання, `DisposeAsync`**

Спека 5.4: `IsConnected => _state == Connected && _link.Client.IsConnected` без замка; `LocalEndPoint` і `RemoteEndPoint` з `_link.Client`. `DisposeAsync` у `ResilientControlClient.Supervisor.cs` за спекою 7.7, поки без кроку 4 (наглядача немає) і без `_restoring`: ідемпотентний (пізніші виклики повертають задачу першого), під `_sync` `_disposed = true`, `Closed`, підміна `_changed`; поза замком `_lifetime.Cancel()`, `await _link.Client.DisposeAsync()`, `await _link.Pump`, `_completion.TrySetResult()`, 1112 з `target`, `_unsolicited.Writer.TryComplete()`. Ніколи не кидає.

- [ ] **Step 8: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~InterfaceParityTests|FullyQualifiedName~ResilientConnectTests|FullyQualifiedName~LoggingOptionsTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 9: Commit**

```bash
git add NetSdr/Control/ResilientControlClientOptions.cs NetSdr/Control/ConnectionRestoredContext.cs NetSdr/Control/ResilientControlClient.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr/Control/ResilientControlClient.Log.cs NetSdr.Tests/Control/Resilient.cs NetSdr.Tests/Control/InterfaceParityTests.cs NetSdr.Tests/Control/ResilientConnectTests.cs NetSdr.Tests/LoggingOptionsTests.cs
git commit -m "feat: add ResilientControlClient with verified connect, line-guarded commands and disposal" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Гігієна лінії і перейняття запізнілих відповідей

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.cs`
- Test: `NetSdr.Tests/Control/ResilientLineTests.cs`
- Create (test): `examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaResilienceTests.cs`

**Interfaces:**
- Consumes: `LateReplyObserver` (5); каркас `Link`, `Exchange`, `CommandExecution`, `Settle`, `Resolve`, `OpenLinkAsync` і `ResilientClientLog` (6); `Resilient`, `PipeConnector` (6).
- Produces: `void OnLateReply(Link link, ControlItemMessage message, bool isNak)` (спостерігач, спека 6.6); `void Expire(object? state)` (колбек таймера, `state` це `Exchange`); `void OnInnerCompleted(Link link)` (продовження на `Completion` кожного внутрішнього клієнта: розв'язує `link.Current` як `Lost` з причиною `link.LossCause` або винятком `Completion`); `Settle` з гілкою `Unanswered`; крок 4a `AttemptAsync` через `exec.Outstanding`. Задача 8 покладається на те, що закритий через `Expire` внутрішній клієнт завершує `Completion` і має `link.LossCause`.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class ResilientLineTests
{
    static readonly TimeSpan Late = TimeSpan.FromMilliseconds(250);   // past ResponseTimeout 150 ms, well before the 750 ms deadline

    [Fact]
    public async Task BusyDevice_LateReplyAdopted_NoResend()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs),
            s => s.OnRequest(RfGain.Code, Resilient.Once(ControlReply.Echo.After(Late))));
        await using (server)
        await using (client)
        {
            var local = client.LocalEndPoint;
            Assert.Equal(-20, (await client.SetAsync(new RfGain(0, -20)).WaitAsync(Limits.Test)).GainDb);
            Assert.Single(server.Received, r => r.Code == RfGain.Code);
            Assert.Equal("NoReplyWaitingForLateReply", Assert.Single(logs.Events(1100)).Value("Reason"));
            Assert.Equal("Reply", Assert.Single(logs.Events(1107)).Value("Outcome"));
            Assert.Empty(logs.Events(1103));
            Assert.Equal(local, client.LocalEndPoint);
        }
    }

    [Fact]
    public async Task BusyDevice_NextCommandForOtherItem_GetsItsOwnReply()
    {
        var delayed = new ConcurrentDictionary<byte, bool>();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s =>
        {
            s.OnRequest(RfGain.Code, Resilient.Once(ControlReply.Echo.After(Late)));
            s.Preload(new InterfaceVersion(529));
            s.OnRequest(FirmwareVersion.Code, r =>
            {
                byte id = r.Payload.Span[0];
                var reply = ControlReply.Bytes(new byte[] { id, (byte)(100 + id), 0 });
                return delayed.TryAdd(id, true) ? reply.After(Late) : reply;
            });
        });
        await using (server)
        await using (client)
        {
            var set = client.SetAsync(new RfGain(0, -20));
            var get = client.GetAsync<InterfaceVersion>();      // queued behind the unanswered Set
            Assert.Equal(-20, (await set.WaitAsync(Limits.Test)).GainDb);
            Assert.Equal(529, (await get.WaitAsync(Limits.Test)).Version);
            for (byte id = 0; id <= 3; id++)                     // as ReadFirmwareAsync asks, each answered late once
            {
                var reply = await client.SendAsync(RequestType.Get, FirmwareVersion.Code, new[] { id }).WaitAsync(Limits.Test);
                Assert.Equal(new byte[] { id, (byte)(100 + id), 0 }, reply.Payload.ToArray());
            }

            Assert.Equal(4, server.Received.Count(r => r.Code == FirmwareVersion.Code));
        }
    }

    [Fact]
    public async Task LateNak_AttributedToItsOwnRequest()
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s =>
        {
            s.OnRequest(SerialNumber.Code, _ => ControlReply.Nak.After(Late));
            s.Preload(new InterfaceVersion(529));
        });
        await using (server)
        await using (client)
        {
            var nak = await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<SerialNumber>().WaitAsync(Limits.Test));
            Assert.Equal(SerialNumber.Code, nak.Code);
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
            var options = new IdentificationOptions { IncludeStandardProbes = false };
            options.Probes.Add(Probes.Item<SerialNumber>());
            options.Probes.Add(Probes.Item<InterfaceVersion>());
            var identity = await DeviceIdentity.ReadAsync(client, options);
            Assert.Equal(SerialNumber.Code, Assert.Single(identity.Unsupported));
            Assert.Equal(2, server.Received.Count(r => r.Code == SerialNumber.Code));   // one per call, none resent
        }
    }

    [Fact]
    public async Task NoWriteWhileUnanswered()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            Assert.Equal(Hex.Parse("04 20 09 00"), await device.ReadRequestAsync());
            await Eventually.ThatAsync(() => logs.Events(1100).Count == 1);   // A timed out; its next attempt adopts
            var b = client.GetAsync<InterfaceVersion>();
            var next = device.ReadRequestAsync();
            await Task.Delay(300);
            Assert.False(next.IsCompleted);
            await device.SendAsync(Resilient.ProductReply);
            Assert.Equal(Hex.Parse("04 20 03 00"), await next);
            await device.SendAsync(Resilient.VersionReply);
            Assert.Equal(0x03524453u, (await a.WaitAsync(Limits.Test)).Value);
            Assert.Equal(529, (await b.WaitAsync(Limits.Test)).Version);
        }
    }

    [Fact]
    public async Task ForeignReply_NotRetried_LineWaitsForRealReply()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            await device.ReadRequestAsync();
            await device.SendAsync(Resilient.VersionReply);                    // Response 0x0003: foreign to A
            await Assert.ThrowsAsync<NetSdrProtocolException>(() => a.WaitAsync(Limits.Test));
            var b = client.GetAsync<InterfaceVersion>();
            var next = device.ReadRequestAsync();
            await Task.Delay(300);
            Assert.False(next.IsCompleted);
            await device.SendAsync(Resilient.ProductReply);                    // A's real reply frees the line
            Assert.Equal(Hex.Parse("04 20 03 00"), await next);
            await device.SendAsync(Resilient.VersionReply);
            Assert.Equal(529, (await b.WaitAsync(Limits.Test)).Version);
            var codes = new List<ushort>();
            for (int i = 0; i < 2; i++) codes.Add((await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code);
            Assert.Equal(new ushort[] { 0x0003, 0x0009 }, codes);         // the foreign frame, then A's real reply
            Assert.False(client.Unsolicited.TryRead(out _));
            Assert.Empty(logs.Events(1100));
        }
    }

    [Fact]
    public async Task ForeignReply_WithoutRealReply_ClosesAfterDeadline()
    {
        var logs = new FakeLoggerFactory();
        var (client, device) = await new PipeConnector().StartAsync(Resilient.Seam(logs));
        await using (client)
        {
            var a = client.GetAsync<ProductId>();
            await device.ReadRequestAsync();
            await device.SendAsync(Resilient.VersionReply);
            await Assert.ThrowsAsync<NetSdrProtocolException>(() => a.WaitAsync(Limits.Test));
            await Eventually.ThatAsync(() => logs.Events(1102).Count == 1);   // 750 ms after the write
            Assert.Equal(LogLevel.Warning, logs.Events(1102)[0].Level);
            Assert.False(device.Client.IsConnected);
            Assert.False(client.IsConnected);
        }
    }
}
```

`examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaResilienceTests.cs` (лише публічні опції і справжній час):

```csharp
public class VegaResilienceTests
{
    static ResilientControlClientOptions Fast() => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(150),
        LateReplyTimeout = TimeSpan.FromMilliseconds(600),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
    };

    static DeviceCatalog<VegaReceiverBase> Catalog() =>
        new DeviceCatalog<VegaReceiverBase>(new IdentificationOptions { Probes = { VegaProbes.Identify(VegaEmulator.DefaultKey) } })
            .Register("Vega v2",
                id => id.ProductId == VegaProtocol.ProductId && id.Get<VegaInfo>().Firmware >= new Version(2, 0),
                (c, id) => new VegaV2Receiver(c, id))
            .Register("Vega v1", id => id.ProductId == VegaProtocol.ProductId, (c, id) => new VegaV1Receiver(c, id));

    static Task<ResilientControlClient> ConnectAsync(VegaEmulator emulator, ResilientControlClientOptions options) =>
        ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), options).WaitAsync(Limits.Test);

    [Fact]
    public async Task Catalog_AttachAsync_OverResilientClient()
    {
        await using var emulator = new VegaEmulator();
        var product = ControlReply.Item(new ProductId(VegaProtocol.ProductId));
        int asked = 0;
        emulator.Server.OnRequest(ProductId.Code, _ => Interlocked.Increment(ref asked) == 1 ? product.After(TimeSpan.FromMilliseconds(250)) : product);
        emulator.Server.OnRequest(SerialNumber.Code, _ => ControlReply.Nak.After(TimeSpan.FromMilliseconds(250)));
        await emulator.StartAsync();
        var client = await ConnectAsync(emulator, Fast());
        VegaReceiverBase device = await Catalog().AttachAsync(client).WaitAsync(Limits.Test);
        Assert.IsType<VegaV2Receiver>(device);
        Assert.Contains(SerialNumber.Code, device.Identity.Unsupported);
        Assert.Equal(1, emulator.Server.Received.Count(r => r.Code == ProductId.Code));
        Assert.Equal(1, emulator.Server.Received.Count(r => r.Code == SerialNumber.Code));
        await device.DisposeAsync();                                           // the device owns the client
        Assert.True(client.Completion.IsCompletedSuccessfully);
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientLineTests|FullyQualifiedName~VegaResilienceTests"`
Expected: FAIL. `NoWriteWhileUnanswered` і `ForeignReply_NotRetried_LineWaitsForRealReply`: кадр B приходить до відповіді A (`Assert.False(next.IsCompleted)`); `BusyDevice_*` і `LateNak_*`: `TimeoutException` замість відлуння чи NAK; `ForeignReply_WithoutRealReply_ClosesAfterDeadline`: немає 1102.

- [ ] **Step 3: Спостерігач, `Settle`, `Expire`, продовження на `Completion`**

`OpenLinkAsync` ставить `inner.LateReplyObserver = (m, nak) => OnLateReply(link, m, nak)` до підключення і `inner.Completion.ContinueWith(_ => OnInnerCompleted(link), TaskScheduler.Default)` після. Тіла за спекою 6.6: `Settle` на `TimeoutException` або `NetSdrProtocolException` при `link.Client.IsConnected` (для чужої відповіді ще `Heard`) під `_sync` позначає ще не розв'язаний обмін `Unanswered` і заводить `tp.CreateTimer(Expire, e, deadline - now, Timeout.InfiniteTimeSpan)` з `deadline = e.SentAt + ResponseTimeout + LateReplyTimeout`; мертвий Link дає `Resolve(Lost(cause))`. `OnLateReply` під `_sync` бере `link.Current`; якщо не розв'язаний, `Resolve(e, isNak ? Nak : Reply(message))` і `Heard`; ніколи не блокує і не кидає. `Expire`: якщо обмін досі `Unanswered`, а Link живий, `link.LossCause = new TimeoutException($"No reply to {type} 0x{code:X4} within {ResponseTimeout + LateReplyTimeout}."); ResilientClientLog.ConnectionUnresponsive(...); /* 1102, Elapsed від SentAt */ _ = link.Client.DisposeAsync(); Resolve(e, Lost);`. `Resolve` звільняє таймер. Порядок замків: внутрішній `_sync` ніколи не тримається, поки виконується код стійкого клієнта (спостерігач викликається вже без нього, спека 4.2).

- [ ] **Step 4: Перейняття в `AttemptAsync`**

Крок 4a спеки 6.5: якщо `exec.Outstanding is { } e`, обнулити його і чекати `e.Late.Task.WaitAsync(t)`; `Reply` повертає повідомлення з 1107 (`Outcome = Reply`, `Late` = час від `SentAt + ResponseTimeout` до розв'язання), `Nak` пише 1107 (`Outcome = Nak`) і кидає `NetSdrNakException(code, type)`, `Lost` кидає `IOException($"The connection to {target} was lost before {type} 0x{code:X4} was answered.", cause)`. Крок 4e: `TimeoutException` при живому з'єднанні ставить `exec.Outstanding = e` і кидає далі; `NetSdrProtocolException` при живому з'єднанні кидається без повтору. `ShouldRetryCommand` уже повторює `TimeoutException` з `Outstanding != null` (задача 6).

- [ ] **Step 5: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientLineTests|FullyQualifiedName~VegaResilienceTests|FullyQualifiedName~InterfaceParityTests|FullyQualifiedName~ResilientConnectTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 6: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.cs NetSdr.Tests/Control/ResilientLineTests.cs examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaResilienceTests.cs
git commit -m "feat: hold the line while a request is unanswered and adopt its late reply" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Наглядач: втрата, перепідключення, backoff, відмова

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.cs` (`WaitForLinkAsync`, `ConnectCoreAsync`, `OpenLinkAsync`), `NetSdr/Control/ResilientControlClient.Supervisor.cs`
- Modify: `NetSdr.Tests/FakeTime.cs`
- Test: `NetSdr.Tests/Control/ResilientReconnectTests.cs`

**Interfaces:**
- Consumes: `OpenLinkAsync`, `VerifyAsync`, `Resolve`, `ClosedException`, `_changed`, `_lastAttemptStart` (6); `Expire`, `OnInnerCompleted`, `link.LossCause` (7).
- Produces:
  - `sealed class ReconnectState(Exception cause, DateTimeOffset lostAt, long lostTimestamp) { public Exception Cause { get; } = cause; public DateTimeOffset LostAt { get; } = lostAt; public long LostTimestamp { get; } = lostTimestamp; public int Attempt { get; set; } public ReconnectPhase Phase { get; set; } }` (члени явні: параметри первинного конструктора класу не властивості, а задача 9 читає `state.Cause` і `state.LostAt`); `static readonly ResiliencePropertyKey<ReconnectState> ReconnectKey = new("NetSdr.Reconnect")`; поле `ResiliencePipeline _reconnect`.
  - `Task SuperviseAsync(Link link)` (спека 7.1, ніколи не кидає), `Task WatchAsync(Link link)` (тут лише чекає `Completion` внутрішнього клієнта, не кидаючи; heartbeat додає задача 10), `void MarkLost(Link link, Exception cause)`, `bool Publish(Link link)`, `void GiveUp(Exception ex, int attempts)`, `ValueTask<Link> ReconnectOnceAsync(ResilienceContext context, ReconnectState state)` (кроки 1-5 і 7-9 спеки 7.4; крок 6 додає задача 9), поле `Task _supervisor`.
  - Тести: `static Task AdvanceUntilAsync(this FakeTimeProvider time, Func<bool> condition, TimeSpan step)` у `FakeTime.cs`.

- [ ] **Step 1: Написати `AdvanceUntilAsync`**

```csharp
/// <summary>
/// Advances fake time by <paramref name="step"/> until <paramref name="condition"/> holds. Pending timers of FakeTimeProvider
/// cannot be seen, so time moves in steps with a real millisecond between them for the released work to run.
/// </summary>
/// <exception cref="TimeoutException">The condition did not hold within Limits.Test of real time.</exception>
public static async Task AdvanceUntilAsync(this FakeTimeProvider time, Func<bool> condition, TimeSpan step)
{
    var clock = Stopwatch.StartNew();
    while (!condition())
    {
        if (clock.Elapsed > Limits.Test) throw new TimeoutException($"The condition was not met within {Limits.Test} of real time.");
        time.Advance(step);
        await Task.Delay(1);
    }
}
```

- [ ] **Step 2: Написати тести, що падають**

```csharp
public class ResilientReconnectTests
{
    [Fact]
    public async Task LostDuringCommand_RetriedAfterRestore()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs), s => s.OnRequest(AfGain.Code, Resilient.DropOnce(s)));
        await using (server)
        await using (client)
        {
            Assert.Equal(7, (await client.SetAsync(new AfGain(0, 7)).WaitAsync(Limits.Test)).Level);
            Assert.Equal(new[] { AfGain.Code, AfGain.Code }, server.Received.WithoutStatus().Select(r => r.Code));
            Assert.Equal("ConnectionLost", Assert.Single(logs.Events(1100)).Value("Reason"));
            Assert.Single(logs.Events(1105));
        }
    }

    [Fact]
    public async Task DeviceDroppedRequest_ConnectionReplaced_CommandResent()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs), s => s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Silent)));
        await using (server)
        await using (client)
        {
            Assert.Equal(3, (await client.SetAsync(new AfGain(0, 3)).WaitAsync(Limits.Test)).Level);
            Assert.Single(logs.Events(1102));
            Assert.Single(logs.Events(1103));
            Assert.Single(logs.Events(1105));
            Assert.Equal(new[] { AfGain.Code, AfGain.Code }, server.Received.WithoutStatus().Select(r => r.Code));
        }
    }

    [Fact]
    public async Task IdleDrop_ReconnectsProactively()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            int oldPort = client.LocalEndPoint!.Port;
            await server.DisconnectClientAsync();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            Assert.True(client.IsConnected);
            Assert.NotEqual(oldPort, client.LocalEndPoint!.Port);
            Assert.Equal(LogLevel.Warning, Assert.Single(logs.Events(1103)).Level);
            Assert.Equal((LogLevel.Information, "1"), (logs.Events(1105)[0].Level, logs.Events(1105)[0].Value("Attempts")));
        }
    }

    // The test server serves one client at a time: a plain client queued behind ours takes the server once ours drops.
    static async Task<NetSdrControlClient> OccupyAsync(NetSdrTestServer server)
    {
        var blocker = new NetSdrControlClient();
        await blocker.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        await server.DisconnectClientAsync();
        return blocker;
    }

    [Fact]
    public async Task Verification_OneClientServerBusy()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            var blocker = await OccupyAsync(server);
            await Eventually.ThatAsync(() => logs.Events(1104).Any(r => r.Value("Phase") == "Verify"));
            Assert.False(client.IsConnected);
            await blocker.DisposeAsync();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
        }
    }

    [Fact]
    public async Task IsConnected_And_EndPoints()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            var (local, remote) = (client.LocalEndPoint, client.RemoteEndPoint);
            var blocker = await OccupyAsync(server);
            await Eventually.ThatAsync(() => logs.Events(1104).Count >= 1);
            Assert.False(client.IsConnected);
            Assert.Equal((local, remote), (client.LocalEndPoint, client.RemoteEndPoint));   // the lost connection's ends
            await blocker.DisposeAsync();
            await Eventually.ThatAsync(() => client.IsConnected);
            Assert.NotEqual(local!.Port, client.LocalEndPoint!.Port);
            Assert.Equal(remote, client.RemoteEndPoint);
        }
    }

    [Fact]
    public async Task Unsolicited_SpansReconnects_InOrder()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            await server.SendUnsolicitedAsync(new AfGain(0, 1));
            await server.SendUnsolicitedAsync(new AfGain(0, 2));
            await server.DisconnectClientAsync();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            await server.SendUnsolicitedAsync(new AfGain(0, 3));
            await server.SendUnsolicitedAsync(new AfGain(0, 4));
            var levels = new List<byte>();
            while (levels.Count < 4)
            {
                var m = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
                if (m.Type == ReplyType.Unsolicited) levels.Add(m.As<AfGain>().Level);
            }

            Assert.Equal(new byte[] { 1, 2, 3, 4 }, levels);
            Assert.False(client.Unsolicited.Completion.IsCompleted);
            await client.DisposeAsync();
            await client.Unsolicited.Completion.WaitAsync(Limits.Test);
        }
    }

    [Fact]
    public async Task ConcurrentCommands_AcrossADrop_InCallOrder()
    {
        int sets = 0;
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s => s.OnRequest(AfGain.Code, _ =>
        {
            if (Interlocked.Increment(ref sets) != 5) return ControlReply.Echo;
            _ = s.DisconnectClientAsync();
            return ControlReply.Silent;
        }));
        await using (server)
        await using (client)
        {
            var calls = Enumerable.Range(1, 10).Select(i => client.SetAsync(new AfGain(0, (byte)i))).ToArray();
            var echoes = await Task.WhenAll(calls).WaitAsync(Limits.Test);
            Assert.Equal(Enumerable.Range(1, 10).Select(i => (byte)i), echoes.Select(e => e.Level));
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 5, 6, 7, 8, 9, 10 },
                server.Received.Where(r => r.Code == AfGain.Code).Select(r => r.Payload.Span[1]));
        }
    }

    [Fact]
    public async Task Backoff_Schedule_And_AntiFlap()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var connector = new PipeConnector(time) { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var (client, device) = await connector.StartAsync(Resilient.Seam(logs, time));
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));               // the first attempt after this loss may start at once
            device.CloseRemote();
            await time.AdvanceUntilAsync(() => connector.Attempts >= 6, TimeSpan.FromMilliseconds(100));
            // Starts at 0, 1, 3, 7, 15 s: consecutive gaps, each late by at most a few 100 ms steps of fake time.
            var starts = connector.AttemptTimes.Skip(1).Take(5).ToArray();
            double[] gaps = [1, 2, 4, 8];
            for (int i = 1; i < 5; i++) Assert.InRange((starts[i] - starts[i - 1]).TotalSeconds, gaps[i - 1], gaps[i - 1] + 0.3);
            Assert.Equal(new[] { 1.0, 2, 4, 8 }, logs.Events(1104).Take(4).Select(r => r.Span("Delay").TotalSeconds));
        }

        // A device that accepts, answers the verification and drops at once gets at most one connection a fake second.
        var flapTime = new FakeTimeProvider();
        var flapping = new PipeConnector(flapTime)
        {
            Serve = async d =>
            {
                await d.ReadRequestAsync();
                await d.SendAsync(Resilient.Nak);
                await Task.Delay(20);
                d.CloseRemote();
            },
        };
        var (flap, _) = await flapping.StartAsync(Resilient.Seam(time: flapTime));
        await using (flap)
        {
            for (int i = 0; i < 100; i++)                        // 10 fake seconds
            {
                flapTime.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(5);
            }

            Assert.InRange(flapping.Attempts, 2, 11);
        }
    }

    [Fact]
    public async Task GiveUp_ReconnectAttemptsExhausted()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var connector = new PipeConnector(time) { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam(logs, time);
        options.CommandTimeout = Timeout.InfiniteTimeSpan;
        options.ReconnectAttempts = 3;
        var (client, device) = await connector.StartAsync(options);
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        await Eventually.ThatAsync(() => !client.IsConnected);
        var waiting = client.GetAsync<InterfaceVersion>();
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, TimeSpan.FromMilliseconds(100));
        Assert.Equal(new[] { 1.0, 2 }, logs.Events(1104).Select(r => r.Span("Delay").TotalSeconds));
        var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(Limits.Test)));
        Assert.IsType<SocketException>(failure.InnerException);
        Assert.Contains("after 3 attempt(s)", failure.Message);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => { _ = client.GetAsync<InterfaceVersion>(); }).InnerException);
        Assert.False(client.IsConnected);
        await client.Unsolicited.Completion.WaitAsync(Limits.Test);
        await client.DisposeAsync();
        Assert.Same(failure, client.Completion.Exception!.InnerException);
        var gaveUp = Assert.Single(logs.Events(1106));
        Assert.Equal((LogLevel.Error, "attempts exhausted"), (gaveUp.Level, gaveUp.Value("Reason")));
    }

    [Fact]
    public async Task Dispose_DuringBackoff()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var connector = new PipeConnector(time) { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var (client, device) = await connector.StartAsync(Resilient.Seam(logs, time));
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        await time.AdvanceUntilAsync(() => logs.Events(1104).Count == 1, TimeSpan.FromMilliseconds(100));
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);   // fake time stands still: Polly waits its 1 s
        Assert.True(client.Completion.IsCompletedSuccessfully);
        Assert.Empty(logs.Events(1106));
    }

    [Fact]
    public async Task Dispose_RacingLoss_200Iterations()
    {
        int unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> count = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Any(x => x.StackTrace?.Contains("ResilientControlClient") == true))
                Interlocked.Increment(ref unobserved);
        };
        TaskScheduler.UnobservedTaskException += count;
        try
        {
            var logs = new FakeLoggerFactory();
            await using var server = new NetSdrTestServer();
            await server.StartAsync();
            for (int i = 0; i < 200; i++)
            {
                var client = await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), Resilient.Fast(logs))
                    .WaitAsync(Limits.Test);
                await Task.WhenAll(server.DisconnectClientAsync(), client.DisposeAsync().AsTask()).WaitAsync(Limits.Test);
                Assert.True(client.Completion.IsCompletedSuccessfully);
            }

            Assert.Empty(logs.Events(1106));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Equal(0, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= count;
        }
    }

    // Review Focus 1.
    [Fact]
    public async Task HostName_ReconnectsByName_EndPointsStayIPv4()
    {
        var logs = new FakeLoggerFactory();
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        var client = await ResilientControlClient.ConnectAsync("127.0.0.1", server.Port, Resilient.Fast(logs)).WaitAsync(Limits.Test);
        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint!.Address);
        await server.DisconnectClientAsync();
        await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint!.Address);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, server.Port), client.RemoteEndPoint);
        _ = DataOutputUdpAddress.For(new IPEndPoint(client.LocalEndPoint.Address, 50_001));   // what a ConnectionRestored callback builds
        await client.DisposeAsync();
        Assert.Equal($"127.0.0.1:{server.Port}", Assert.Single(logs.Events(1112)).Value("Target"));
    }

    // Review Focus 2.
    [Fact]
    public async Task LongOutage_SeventyAttempts_DelayCappedThenRecovers()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        bool back = false;
        var connector = new PipeConnector(time)
        {
            Before = (n, _) => n == 1 || Volatile.Read(ref back) ? Task.CompletedTask : Resilient.Refused(),
            Serve = d => d.NakEverythingAsync(),
        };
        var options = Resilient.Seam(logs, time);
        options.UseJitter = true;                                // the production default
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            device.CloseRemote();
            for (int chunk = 1; chunk <= 7; chunk++)             // each AdvanceUntilAsync stays within Limits.Test
                await time.AdvanceUntilAsync(() => logs.Events(1104).Count >= chunk * 10, TimeSpan.FromSeconds(30));
            Assert.All(logs.Events(1104), r => Assert.InRange(r.Span("Delay"), TimeSpan.Zero, TimeSpan.FromSeconds(30)));
            Volatile.Write(ref back, true);
            await time.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, TimeSpan.FromSeconds(30));
            Assert.True(client.IsConnected);
            Assert.Equal((logs.Events(1104).Count + 1).ToString(), logs.Events(1105)[0].Value("Attempts"));
            Assert.Empty(logs.Events(1106));
        }
    }

    // Review Focus 3.
    [Fact]
    public async Task Dispose_FailsWaitingCommands_WithObjectDisposed()
    {
        var connector = new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam();
        options.CommandTimeout = Timeout.InfiniteTimeSpan;
        var (client, device) = await connector.StartAsync(options);
        device.CloseRemote();
        await Eventually.ThatAsync(() => !client.IsConnected);
        var waitingForLink = client.GetAsync<InterfaceVersion>();
        var queued = client.GetAsync<ProductId>();
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waitingForLink.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(Limits.Test));
        Assert.Throws<ObjectDisposedException>(() => { _ = client.GetAsync<InterfaceVersion>(); });

        var (live, pipe) = await new PipeConnector().StartAsync(Resilient.Seam());
        var inFlight = live.GetAsync<ProductId>();
        await pipe.ReadRequestAsync();
        await live.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inFlight.WaitAsync(Limits.Test));
    }
}
```

- [ ] **Step 3: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientReconnectTests"`
Expected: FAIL: немає 1103/1105, `Eventually` кидає `TimeoutException`, команди після обриву не завершуються.

- [ ] **Step 4: Pipeline перепідключення і спроба**

Конструктор будує `_reconnect` дослівно за спекою 8 (`MaxRetryAttempts = ReconnectAttempts - 1`, експоненційний від 1 с до 30 с, `UseJitter` з опцій, `ShouldHandle` пропускає `FatalRestoreException` і скасований токен, `OnRetry` бере `ReconnectState` з `context.Properties` і пише 1104 з `a.RetryDelay`); при `ReconnectAttempts == 1` це `ResiliencePipeline.Empty`. `FatalRestoreException` з'являється в задачі 9, тож тут умова лише щодо скасування, а задача 9 її доповнює. `ReconnectOnceAsync` за спекою 7.4: `state.Attempt++`; підлога: `remaining = 1 s - tp.GetElapsedTime(_lastAttemptStart, tp.GetTimestamp())`, при `remaining > 0` `await Task.Delay(remaining, tp, context.CancellationToken)` усередині `try`; `_lastAttemptStart = tp.GetTimestamp()`; `OpenLinkAsync(p => state.Phase = p, context.CancellationToken)`; крок 7 (`!inner.IsConnected` дає `IOException("The connection was lost while it was being restored.", link.LossCause)`); крок 9 через `OpenLinkAsync`. `OpenLinkAsync` після запуску pump під `_sync` кидає `ObjectDisposedException` у Closed або ставить `_restoring = link`, а в разі невдачі очищає `_restoring`, якщо це цей Link.

- [ ] **Step 5: Наглядач, публікація, відмова**

`SuperviseAsync` дослівно за псевдокодом спеки 7.1: `WatchAsync`, причина `link.LossCause ?? link.Client.Completion.Exception?.InnerException ?? new IOException("Connection closed.")`, `lostAt = tp.GetUtcNow()` і `lostTimestamp = tp.GetTimestamp()`, `MarkLost`, 1103, `await link.Client.DisposeAsync()`, `await link.Pump`, `ReconnectState`, `_reconnect.ExecuteAsync` з контекстом `ResilienceContextPool.Shared.Get(_lifetime.Token)` і `context.Properties.Set(ReconnectKey, state)`, `Publish`, 1105 (`Attempts = state.Attempt`, `Downtime = tp.GetElapsedTime(state.LostTimestamp, tp.GetTimestamp())`); `catch when (_lifetime.IsCancellationRequested)` нічого не робить, інший `catch` викликає `GiveUp(ex, state.Attempt)`. `MarkLost` і `Publish` за таблицею спеки 6.1 (`MarkLost` записує `_lastLoss`, `Publish` очищає `_restoring` і в Closed закриває Link поза замком). `GiveUp` за спекою 7.6 з `failure = new IOException($"Gave up reconnecting to {target} after {attempts} attempt(s).", cause)` і 1106 з `Reason = "attempts exhausted"` і `failure` як винятком. Кожен перехід підміняє `_changed` під замком і завершує старий поза ним.

- [ ] **Step 6: Очікування з'єднання, старт наглядача, повне закриття**

`WaitForLinkAsync` за спекою 6.5 крок 4b: при `Reconnecting` або мертвому внутрішньому клієнті чекати захоплений у тій самій критичній секції `_changed` (з токеном `t`) і перевіряти знову; Closed дає `ClosedException()` (`_failure` після відмови). `ConnectCoreAsync` після публікації першого Link запускає `_supervisor = SuperviseAsync(link)`. `DisposeAsync` отримує кроки 3 і 4 спеки 7.7: закриває і `_link`, і `_restoring`, потім чекає `_supervisor`; після відмови `_completion` зберігає помилку.

- [ ] **Step 7: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientReconnectTests|FullyQualifiedName~ResilientLineTests|FullyQualifiedName~ResilientConnectTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 8: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr.Tests/FakeTime.cs NetSdr.Tests/Control/ResilientReconnectTests.cs
git commit -m "feat: supervise the connection and reconnect with backoff, a 1 s floor and give-up" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Колбек `ConnectionRestored`

**Files:**
- Create: `NetSdr/Control/RestoreSession.cs`
- Modify: `NetSdr/Control/ResilientControlClient.cs` (захист від повторного входу, шлях сесії), `NetSdr/Control/ResilientControlClient.Supervisor.cs` (фаза `Restore`, відмова через повторний вхід, `DisposeAsync` з колбеку)
- Test: `NetSdr.Tests/Control/ResilientRestoreTests.cs`, `NetSdr.Tests/Control/ResilientReconnectTests.cs`, `NetSdr.Tests/EndToEndTests.cs`, `examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaResilienceTests.cs`

**Interfaces:**
- Consumes: `ReconnectOnceAsync`, `ReconnectState`, `Publish`, `GiveUp`, `DisposeAsync`, `_reconnect` (8); `ConnectionRestoredContext`, `RunAsync`, `WaitForLinkAsync`, `CommandExecution.Bound`, `ShouldRetryCommand`, `ClosedException` (6); лінія і перейняття (7).
- Produces:
  - Вкладені приватні: `sealed class RestoreSession(ResilientControlClient owner, Link link) : INetSdrControlClient` з `void End()`; `sealed class RestoreScope(ResilientControlClient owner)` з `Owner`, `bool Active`, `Exception? Reentered`; `sealed class FatalRestoreException(Exception reentrancy) : Exception`; поле `readonly AsyncLocal<RestoreScope?> _restoreScope`; метод `void ThrowIfReentrant()`.

- [ ] **Step 1: Доповнити три тести задачі 8 колбеком**

У `ResilientReconnectTests`:
- `LostDuringCommand_RetriedAfterRestore`: опції `Resilient.Fast(logs)` з `ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new RfGain(0, -10), ct)`; порядок стає `new[] { AfGain.Code, RfGain.Code, AfGain.Code }`.
- `DeviceDroppedRequest_ConnectionReplaced_CommandResent`: той самий колбек плюс лічильник викликів; `Assert.Equal(1, restored)` і порядок `AfGain, RfGain, AfGain`.
- `IdleDrop_ReconnectsProactively`: колбек `(ctx, _) => { seen = ctx; Interlocked.Increment(ref restored); return Task.CompletedTask; }`; `Assert.Equal(1, restored)`, `Assert.NotEqual(default, seen!.LostAt)`, `Assert.IsAssignableFrom<IOException>(seen.Cause)`.

- [ ] **Step 2: Написати нові тести, що падають**

```csharp
public class ResilientRestoreTests
{
    static ResilientControlClientOptions Restoring(FakeLoggerFactory logs, Func<ConnectionRestoredContext, CancellationToken, Task> callback)
    {
        var options = Resilient.Fast(logs);
        options.ConnectionRestored = callback;
        return options;
    }

    static async Task DropAndRestoreAsync(NetSdrTestServer server, FakeLoggerFactory logs)
    {
        await server.DisconnectClientAsync();
        await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
    }

    [Fact]
    public async Task Callback_Throws_RetriedOnNewConnection()
    {
        var logs = new FakeLoggerFactory();
        int calls = 0;
        var ports = new ConcurrentQueue<int>();
        var (server, client) = await Resilient.StartAsync(Restoring(logs, (ctx, _) =>
        {
            ports.Enqueue(ctx.Client.LocalEndPoint!.Port);
            return Interlocked.Increment(ref calls) == 1 ? Task.FromException(new ApplicationException("restore failed")) : Task.CompletedTask;
        }));
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
        }

        var failed = Assert.Single(logs.Events(1104));
        Assert.Equal("Restore", failed.Value("Phase"));
        Assert.IsType<ApplicationException>(failed.Exception);
        Assert.Equal(2, ports.Distinct().Count());              // the third connection runs the second callback
        Assert.Equal((2, 1), (logs.Events(1110).Count, logs.Events(1111).Count));
    }

    [Fact]
    public async Task Callback_Nak_RetriedOnNewConnection()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Restoring(logs, (ctx, ct) => ctx.Client.SetAsync(new AfGain(0, 5), ct)), s =>
        {
            s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Nak));
            s.Preload(new InterfaceVersion(529));
        });
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
            var failed = Assert.Single(logs.Events(1104));
            Assert.Equal("Restore", failed.Value("Phase"));
            Assert.IsType<NetSdrNakException>(failed.Exception);
            Assert.Equal(2, server.Received.Count(r => r.Code == AfGain.Code));
            Assert.Empty(logs.Events(1106));
            Assert.False(client.Completion.IsCompleted);
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
        }
    }

    [Fact]
    public async Task Callback_PersistentNak_GivesUpOnlyWhenAttemptsRunOut()
    {
        var endless = await RunAsync(attempts: null, until: l => l.Events(1104).Count >= 10);
        Assert.Empty(endless.Events(1106));
        Assert.All(endless.Events(1104), r => Assert.IsType<NetSdrNakException>(r.Exception));

        var limited = await RunAsync(attempts: 3, until: l => l.Events(1106).Count == 1);
        Assert.Equal(2, limited.Events(1104).Count);
        Assert.IsType<NetSdrNakException>(Assert.Single(limited.Events(1106)).Exception!.InnerException);

        static async Task<FakeLoggerFactory> RunAsync(int? attempts, Func<FakeLoggerFactory, bool> until)
        {
            var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
            var connector = new PipeConnector(time) { Serve = d => d.NakEverythingAsync() };
            var options = Resilient.Seam(logs, time);
            options.ResponseTimeout = TimeSpan.FromHours(1);   // 5 s fake-time steps must not time out a request the pipe NAKs in real time
            options.ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new AfGain(0, 5), ct);
            if (attempts is { } n) options.ReconnectAttempts = n;
            var (client, device) = await connector.StartAsync(options);
            await using (client)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                device.CloseRemote();
                await time.AdvanceUntilAsync(() => until(logs), TimeSpan.FromSeconds(5));
            }

            return logs;
        }
    }

    [Fact]
    public async Task Callback_CallsResilientClient_GivesUpWithClearMessage()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        Exception? thrown = null;
        var started = await Resilient.StartAsync(Restoring(logs, (ctx, ct) =>
        {
            try { _ = client!.GetAsync<InterfaceVersion>(ct); }
            catch (InvalidOperationException e) { thrown = e; throw; }   // thrown synchronously
            return Task.CompletedTask;
        }));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await started.Server.DisconnectClientAsync();
            var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.Contains("ConnectionRestored called the ResilientControlClient instead of context.Client", failure.Message);
            Assert.Same(thrown, failure.InnerException);
        }

        Assert.Equal("ConnectionRestored called the ResilientControlClient", Assert.Single(logs.Events(1106)).Value("Reason"));
    }

    [Fact]
    public async Task Callback_CatchesReentrancy_StillGivesUp()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        var started = await Resilient.StartAsync(Restoring(logs, (ctx, ct) =>
        {
            try { _ = client!.GetAsync<InterfaceVersion>(ct); }
            catch (InvalidOperationException) { }                       // swallowed: the client still gives up
            return Task.CompletedTask;
        }));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await started.Server.DisconnectClientAsync();
            await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        }

        Assert.Single(logs.Events(1106));
    }

    [Fact]
    public async Task Callback_FireAndForgetAfterReturn_Allowed()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        Task<InterfaceVersion>? later = null;
        var started = await Resilient.StartAsync(Restoring(logs, (ctx, _) =>
        {
            later = Task.Run(async () =>
            {
                await Eventually.ThatAsync(() => client!.IsConnected);   // after the callback returned and the link was published
                return await client!.GetAsync<InterfaceVersion>();
            });
            return Task.CompletedTask;
        }), s => s.Preload(new InterfaceVersion(529)));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await DropAndRestoreAsync(started.Server, logs);
            Assert.Equal(529, (await later!.WaitAsync(Limits.Test)).Version);
            Assert.Empty(logs.Events(1106));
        }
    }

    [Fact]
    public async Task Callback_SessionRules()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        ConnectionRestoredContext? seen = null;
        bool? connectedInside = null;
        int sessionPort = 0;
        var started = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            (seen, sessionPort, connectedInside) = (ctx, ctx.Client.LocalEndPoint!.Port, client!.IsConnected);
            await ctx.Client.DisposeAsync();                        // does nothing
            await ctx.Client.GetAsync<InterfaceVersion>(ct);
        }), s => s.Preload(new InterfaceVersion(529)));
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            int oldPort = client.LocalEndPoint!.Port;
            await DropAndRestoreAsync(started.Server, logs);
            Assert.NotEqual(oldPort, sessionPort);
            Assert.False(connectedInside);
            Assert.True(client.IsConnected);
            Assert.IsAssignableFrom<IOException>(seen!.Cause);
            Assert.Throws<InvalidOperationException>(() => { _ = seen.Client.GetAsync<InterfaceVersion>(); });
        }
    }

    [Fact]
    public async Task Callback_ConnectionLostDuringIt()
    {
        var logs = new FakeLoggerFactory();
        Exception? first = null;
        var (server, client) = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            try { await ctx.Client.SetAsync(new AfGain(0, 5), ct); }
            catch (Exception e) { first ??= e; throw; }
        }), s => s.OnRequest(AfGain.Code, Resilient.DropOnce(s)));
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
        }

        Assert.IsAssignableFrom<IOException>(first);
        Assert.Empty(logs.Events(1100));                          // failed at once, not after a response timeout
        Assert.Equal("Restore", Assert.Single(logs.Events(1104)).Value("Phase"));
    }

    [Fact]
    public async Task CommandDuringRestore_WaitsForCallback()
    {
        var logs = new FakeLoggerFactory();
        var inCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client) = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            inCallback.TrySetResult();
            await gate.Task.WaitAsync(ct);
            await ctx.Client.SetAsync(new AfGain(0, 5), ct);
        }), s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            await server.DisconnectClientAsync();
            await inCallback.Task.WaitAsync(Limits.Test);
            var get = client.GetAsync<InterfaceVersion>();
            await Task.Delay(300);
            Assert.DoesNotContain(server.Received, r => r.Code == InterfaceVersion.Code);
            gate.SetResult();
            Assert.Equal(529, (await get.WaitAsync(Limits.Test)).Version);
            Assert.Equal(new[] { AfGain.Code, InterfaceVersion.Code }, server.Received.WithoutStatus().Select(r => r.Code));
        }
    }

    [Fact]
    public async Task Dispose_DuringCallback()
    {
        var logs = new FakeLoggerFactory();
        var inCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool tokenCancelled = false;
        Exception? afterDispose = null;
        var (server, client) = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            inCallback.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { tokenCancelled = true; }
            try { await ctx.Client.GetAsync<InterfaceVersion>(); } catch (Exception e) { afterDispose = e; }
        }));
        await using (server)
        {
            await server.DisconnectClientAsync();
            await inCallback.Task.WaitAsync(Limits.Test);
            await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
            Assert.True(tokenCancelled);
            Assert.IsType<ObjectDisposedException>(afterDispose);
            Assert.True(client.Completion.IsCompletedSuccessfully);
            Assert.Empty(logs.Events(1106));
            await Loopback.AssertServerFreeAsync(server);
        }
    }

    [Fact]
    public async Task Dispose_FromInsideCallback_NoDeadlock()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        bool disposedInside = false;
        var started = await Resilient.StartAsync(Restoring(logs, async (ctx, ct) =>
        {
            await client!.DisposeAsync().AsTask().WaitAsync(Limits.Test);   // returns without waiting for the supervisor
            disposedInside = true;
        }));
        client = started.Client;
        await using (started.Server)
        {
            await started.Server.DisconnectClientAsync();
            await Eventually.ThatAsync(() => disposedInside);
            Assert.True(client.Completion.IsCompletedSuccessfully);
            Assert.False(client.IsConnected);
            Assert.Empty(logs.Events(1105));
            Assert.Empty(logs.Events(1106));
            await Loopback.AssertServerFreeAsync(started.Server);
        }
    }

    // Review Focus 4.
    [Theory]
    [InlineData("throw")]
    [InlineData("null")]
    public async Task Callback_SyncThrowOrNullTask_FailsOnlyTheAttempt(string kind)
    {
        var logs = new FakeLoggerFactory();
        int calls = 0;
        var (server, client) = await Resilient.StartAsync(Restoring(logs, (ctx, ct) =>
            Interlocked.Increment(ref calls) > 1 ? Task.CompletedTask
            : kind == "throw" ? throw new ApplicationException("not async") : (Task)null!));
        await using (server)
        await using (client)
        {
            await DropAndRestoreAsync(server, logs);
            Assert.Equal("Restore", Assert.Single(logs.Events(1104)).Value("Phase"));
            Assert.Equal(2, calls);
            Assert.Empty(logs.Events(1106));
            Assert.False(client.Completion.IsCompleted);
        }
    }
}
```

`NetSdr.Tests/EndToEndTests.cs`:

```csharp
[Fact]
public async Task EndToEnd_StreamResumesAfterReconnect()
{
    using var c = new PacketCollector();
    var logs = new FakeLoggerFactory();
    var options = Resilient.Fast(logs);
    options.ConnectionRestored = async (ctx, ct) =>
    {
        await ctx.Client.SetAsync(DataOutputUdpAddress.For(new IPEndPoint(ctx.Client.LocalEndPoint!.Address, c.EndPoint.Port)), ct);
        await ctx.Client.SetAsync(ReceiverState.Start(complex: true, bits24: false), ct);
    };
    var (server, client) = await Resilient.StartAsync(options);
    await using (server)
    await using (client)
    {
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 10);
        await server.DisconnectClientAsync();
        await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
        await Eventually.ThatAsync(() => c.Packets.Count(p => p.Info.IsCaptureStart) >= 2);
        Assert.Equal(0, c.Receiver.Statistics.Lost);
        Assert.Equal(0, c.Receiver.Statistics.HandlerErrors);
    }
}
```

У `VegaResilienceTests`:

```csharp
[Fact]
public async Task Vega_ReconnectDuringIdentification()
{
    await using var emulator = new VegaEmulator();
    int asked = 0;
    emulator.Server.OnRequest<VegaFirmwareInfo>(_ =>
    {
        if (Interlocked.Increment(ref asked) > 1) return ControlReply.Item(new VegaFirmwareInfo(200));
        _ = emulator.Server.DisconnectClientAsync();
        return ControlReply.Silent;
    });
    await emulator.StartAsync();
    var options = Fast();
    options.ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey), ct);  // always: session state
    var client = await ConnectAsync(emulator, options);
    await using var device = await Catalog().AttachAsync(client).WaitAsync(Limits.Test);
    Assert.IsType<VegaV2Receiver>(device);
    var codes = emulator.Server.Received.Where(r => r.Code != StatusCodes.Code).Select(r => r.Code).ToList();
    int firstInfo = codes.IndexOf(VegaProtocol.FirmwareInfoCode);
    int unlockAfterDrop = codes.IndexOf(VegaProtocol.VendorUnlockCode, firstInfo + 1);
    Assert.True(unlockAfterDrop > firstInfo);
    Assert.True(codes.IndexOf(VegaProtocol.FirmwareInfoCode, firstInfo + 1) > unlockAfterDrop);
}

[Fact]
public async Task Vega_WrapperOverContextClient_StartsStream()
{
    await using var emulator = new VegaEmulator();
    await emulator.StartAsync();
    DeviceIdentity? identity = null;
    bool streamed = false;
    var options = Fast();
    options.ConnectionRestored = async (ctx, ct) =>
    {
        await ctx.Client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey), ct);
        if (identity is not { } id) return;
        await using var wrapper = new VegaV2Receiver(ctx.Client, id);           // disposing it leaves the connection open
        await wrapper.StartStreamAsync(new IPEndPoint(IPAddress.Loopback, 50_999), 7_100_000, 200_000, ct);
        streamed = true;
    };
    var client = await ConnectAsync(emulator, options);
    await using var device = await Catalog().AttachAsync(client).WaitAsync(Limits.Test);
    identity = device.Identity;
    await emulator.Server.DisconnectClientAsync();
    await Eventually.ThatAsync(() => streamed && client.IsConnected);
    Assert.Contains(emulator.Server.Received, r => r.Code == ReceiverState.Code && r.Type == RequestType.Set);
    await device.GetLabelAsync().WaitAsync(Limits.Test);                        // the connection still serves the device
}
```

- [ ] **Step 3: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientRestoreTests|FullyQualifiedName~ResilientReconnectTests|FullyQualifiedName~EndToEndTests|FullyQualifiedName~VegaResilienceTests"`
Expected: FAIL: колбек ніколи не викликається (`restored` дорівнює 0, `seen` дорівнює `null`, `Eventually` у тестах колбеку кидає `TimeoutException`, повторного входу немає, тож 1106 не пишеться).

- [ ] **Step 4: Сесія і область колбеку**

`RestoreSession` (спека 5.3): кожен виклик спершу перевіряє `End()` (`InvalidOperationException("ConnectionRestoredContext.Client can be used only until the ConnectionRestored callback completes.")`), потім закриття власника (`ObjectDisposedException`), кодує так само, як власник (ті самі статичні помічники кодування), і йде в `RunAsync` з `Bound = link`. `Unsolicited` і `Completion` власника; `IsConnected => !ended && link.Client.IsConnected`; кінці з `link.Client`; `DisposeAsync` нічого не робить. Шлях сесії в `RunAsync` і `AttemptAsync` за спекою 6.2 і 6.5: без admission, `WaitForLinkAsync` повертає `Bound` (мертвий дає `IOException`, завершена сесія `InvalidOperationException`), `ShouldRetryCommand` не повторює `IOException` прив'язаного виконання.

- [ ] **Step 5: Фаза `Restore`, повторний вхід, відмова, закриття з колбеку**

`ReconnectOnceAsync` отримує крок 6 спеки 7.4 між перевіркою і кроком 7: `phase = Restore`, `_restoreScope.Value = scope = new RestoreScope(this) { Active = true }`, `session`, 1110, виклик колбеку з `new ConnectionRestoredContext(session, state.Cause, state.LostAt)` і `_lifetime.Token`; синхронний виняток стає `Task.FromException(ex)`, `null` стає `Task.FromException(new InvalidOperationException("ConnectionRestored returned null instead of a task."))`; `finally`: `scope.Active = false`, `session.End()`, `_restoreScope.Value = null`; 1111 лише при успіху; потім `if (scope.Reentered is { } ex) throw new FatalRestoreException(ex)`, хоч би як завершився колбек. `ThrowIfReentrant()` першим рядком кожного з п'яти публічних методів команди: якщо `_restoreScope.Value` це `{ Active: true }` з `Owner == this`, створити `InvalidOperationException("Inside ConnectionRestored send requests through context.Client; the ResilientControlClient is waiting for this callback.")`, записати в `scope.Reentered` і кинути синхронно. `ShouldHandle` перепідключення не повторює `FatalRestoreException`. `GiveUp` за спекою 7.6: для `FatalRestoreException` причина це її `InnerException`, повідомлення `$"Gave up reconnecting to {target}: ConnectionRestored called the ResilientControlClient instead of context.Client."`, 1106 з `Reason = "ConnectionRestored called the ResilientControlClient"`. `DisposeAsync`: виклик з активної області колбеку (спека 7.5) запускає закриття, закриває обидва внутрішні клієнти і повертається, не чекаючи `_supervisor`.

- [ ] **Step 6: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientRestoreTests|FullyQualifiedName~ResilientReconnectTests|FullyQualifiedName~EndToEndTests|FullyQualifiedName~VegaResilienceTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 7: Commit**

```bash
git add NetSdr/Control/RestoreSession.cs NetSdr/Control/ResilientControlClient.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr.Tests/Control/ResilientRestoreTests.cs NetSdr.Tests/Control/ResilientReconnectTests.cs NetSdr.Tests/EndToEndTests.cs examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaResilienceTests.cs
git commit -m "feat: run ConnectionRestored on the verified connection before publishing it" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Heartbeat і контракт логування стійкого клієнта

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.Supervisor.cs` (`WatchAsync`)
- Test: `NetSdr.Tests/Control/ResilientHeartbeatTests.cs`

**Interfaces:**
- Consumes: `StartExchange`, `Link.Wire`, `Link.LastHeard`, `Exchange.Late`, `Settle`, `Expire` (6, 7); `SuperviseAsync` (8); `ResilientClientLog.HeartbeatMissed`, `LateReplyDrained` з `LateOwner.Heartbeat` (6).
- Produces: `WatchAsync(Link link)` з heartbeat за спекою 7.3; сигнатура без змін.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class ResilientHeartbeatTests
{
    static int StatusRequests(NetSdrTestServer server) => server.Received.Count(r => r.Code == StatusCodes.Code);

    [Fact]
    public async Task Heartbeat_IdleNak_Alive()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));   // the bare server NAKs 0x0005
        await using (server)
        await using (client)
        {
            await Task.Delay(1000);                                                   // ten intervals of silence
            Assert.InRange(StatusRequests(server) - 1, 5, 11);                        // minus the verification
            Assert.Empty(logs.Events(1101));
            Assert.Empty(logs.Events(1103));
        }
    }

    [Fact]
    public async Task Heartbeat_SkippedWhileTrafficFlows()
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s => s.Preload(new InterfaceVersion(529)));
        await using (server)
        await using (client)
        {
            for (int i = 0; i < 20; i++)
            {
                await client.GetAsync<InterfaceVersion>();
                await Task.Delay(50);
            }

            Assert.Equal(1, StatusRequests(server));
        }
    }

    [Fact]
    public async Task Heartbeat_Silent_UnpluggedCable()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            server.OnRequest(StatusCodes.Code, _ => ControlReply.Silent);           // after the verification passed
            await Eventually.ThatAsync(() => logs.Events(1101).Count == 1);
            int written = StatusRequests(server);
            await Task.Delay(300);                                                    // still before the 750 ms deadline
            Assert.Equal(written, StatusRequests(server));                            // no second heartbeat while one is unanswered
            await Eventually.ThatAsync(() => logs.Events(1103).Count == 1);
            Assert.Single(logs.Events(1102));
            Assert.Single(logs.Events(1101));
            await Eventually.ThatAsync(() => logs.Events(1104).Any(r => r.Value("Phase") == "Verify"));
        }
    }

    [Fact]
    public async Task Heartbeat_LateReply_NoReconnect()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs));
        await using (server)
        await using (client)
        {
            server.OnRequest(StatusCodes.Code, _ => ControlReply.Bytes(new[] { StatusCodes.Idle }).After(TimeSpan.FromMilliseconds(250)));
            await Eventually.ThatAsync(() => logs.Events(1108).Count >= 1);
            Assert.Equal(("Heartbeat", "Reply"), (logs.Events(1108)[0].Value("Owner"), logs.Events(1108)[0].Value("Outcome")));
            Assert.NotEmpty(logs.Events(1101));
            await Task.Delay(500);
            Assert.Empty(logs.Events(1103));
        }
    }

    [Fact]
    public async Task Heartbeat_Disabled()
    {
        var options = Resilient.Fast();
        options.HeartbeatInterval = Timeout.InfiniteTimeSpan;
        var (server, client) = await Resilient.StartAsync(options);
        await using (server)
        await using (client)
        {
            await Task.Delay(500);
            Assert.Equal(1, StatusRequests(server));
        }
    }

    [Fact]
    public async Task ResilientLogging_LevelsEventIdsCategories()
    {
        const string Outer = "NetSdr.Control.ResilientControlClient", Inner = "NetSdr.Control.NetSdrControlClient";

        // A busy reply, a silent heartbeat and a silent verification, then a NAK: 1100, 1107, 1101, 1102, 1103, 1104, 1105.
        var logs = new FakeLoggerFactory(LogLevel.Debug);
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs),
            s => s.OnRequest(RfGain.Code, Resilient.Once(ControlReply.Echo.After(TimeSpan.FromMilliseconds(250)))));
        await using (server)
        {
            await client.SetAsync(new RfGain(0, -20)).WaitAsync(Limits.Test);
            // Echoed at once: the inner client writes Debug 1003 and 1004 with Item = RfGain.
            Assert.Equal(-10, (await client.SetAsync(new RfGain(0, -10)).WaitAsync(Limits.Test)).GainDb);
            server.OnRequest(StatusCodes.Code, Resilient.FirstTimes(2, ControlReply.Silent, ControlReply.Nak));
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            await client.DisposeAsync();
        }

        void Expect(FakeLoggerFactory from, int id, LogLevel level, string category)
        {
            Assert.NotEmpty(from.Events(id));
            Assert.All(from.Events(id), r => Assert.Equal((level, category), (r.Level, r.Category)));
        }

        foreach (int id in new[] { 1100, 1101, 1102, 1103, 1104 }) Expect(logs, id, LogLevel.Warning, Outer);
        foreach (int id in new[] { 1105, 1109, 1112 }) Expect(logs, id, LogLevel.Information, Outer);
        Expect(logs, 1107, LogLevel.Debug, Outer);
        foreach (int id in new[] { 1000, 1001, 1003, 1004, 1005, 1006 }) Expect(logs, id, LogLevel.Debug, Inner);
        Assert.Contains(logs.Events(1003), r => r.Value("Item") == "RfGain");
        Assert.Contains(logs.Events(1004), r => r.Value("Item") == "RfGain");
        Assert.Empty(logs.Events(1010).Concat(logs.Events(1011)).Concat(logs.Events(1106)));

        // The device closes the connection: the inner fault is Debug; Trace shows the frames.
        var traced = new FakeLoggerFactory(LogLevel.Trace);
        var (server2, client2) = await Resilient.StartAsync(Resilient.Fast(traced));
        await using (server2)
        await using (client2)
        {
            await server2.DisconnectClientAsync();
            await Eventually.ThatAsync(() => traced.Events(1105).Count == 1);
        }

        Expect(traced, 1002, LogLevel.Debug, Inner);
        Assert.NotEmpty(traced.Events(1010));
        Assert.NotEmpty(traced.Events(1011));

        // Giving up: Error 1106 exactly once.
        var gaveUp = new FakeLoggerFactory();
        var options = Resilient.Seam(gaveUp);
        options.ReconnectAttempts = 1;
        var (lost, device) = await new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() }.StartAsync(options);
        device.CloseRemote();
        await Assert.ThrowsAsync<IOException>(() => lost.Completion.WaitAsync(Limits.Test));
        await lost.DisposeAsync();
        Expect(gaveUp, 1106, LogLevel.Error, Outer);
        Assert.Single(gaveUp.Events(1106));
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientHeartbeatTests"`
Expected: FAIL: `Heartbeat_IdleNak_Alive` бачить 0 heartbeat, `Heartbeat_Silent_UnpluggedCable` і логування не дочікуються 1101.

- [ ] **Step 3: Реалізувати heartbeat у `WatchAsync`**

Алгоритм дослівно за спекою 7.3: цикл, поки `!link.Client.Completion.IsCompleted`; `Infinite` лише чекає `Completion`; `due = LastHeard + HeartbeatInterval`, до нього `await Task.WhenAny(Completion, Task.Delay(due - now, tp, _lifetime.Token))`; `link.Wire.Wait(0)` (неблокуюча спроба, не обганяє команду в черзі); якщо Wire зайнятий, чекати ще `HeartbeatInterval`; інакше `StartExchange(link, Get, StatusCodes.Code, empty, nameof(StatusCodes))` і чекати `e.Request` з межею `ResponseTimeout`. Response або NAK означають "живий"; `TimeoutException` на живому з'єднанні пише 1101 (`ResponseTimeout`, `LateReplyTimeout`) і ставить на `e.Late` продовження, що пише 1108 (`Owner = Heartbeat`, `Outcome` з результату), якщо обмін закінчився `Reply` або `Nak`; далі `Expire` робить свою справу. Чужа відповідь лишає обмін `Unanswered`. Решта означає мертве з'єднання, про яке скаже `Completion`. Затримки через `_lifetime.Token`: `OperationCanceledException` під час `DisposeAsync` наглядач уже ковтає (спека 7.1).

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientHeartbeatTests|FullyQualifiedName~Resilient"`
Expected: PASS. Потім `dotnet test NetSdr.sln`: усе зелене (тести з `Resilient.Fast()` тепер ідуть із heartbeat 100 мс і фільтрують 0x0005 через `WithoutStatus`).

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr.Tests/Control/ResilientHeartbeatTests.cs
git commit -m "feat: probe idle connections with a Get 0x0005 heartbeat" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: `CommandTimeout`, скасування і стрес

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.cs` (`RunAsync`, `AttemptAsync`)
- Test: `NetSdr.Tests/Control/ResilientTimeoutTests.cs`

**Interfaces:**
- Consumes: `RunAsync`, `AttemptAsync`, `CommandExecution.LastError` (6); `Exchange.Late`, `Outstanding` (7); `_lastLoss` (8); шлях сесії (9); `ResilientClientLog.LateReplyDrained` з `LateOwner.CancelledCaller` (6).
- Produces: дедлайн `CommandTimeout` і повний переклад винятків кроку 7 спеки 6.5; 1108 для скасованого викликача. Сигнатури без змін.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class ResilientTimeoutTests
{
    [Fact]
    public async Task CommandTimeout_DuringOutage()
    {
        var connector = new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam();
        options.CommandTimeout = TimeSpan.FromMilliseconds(300);
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            device.CloseRemote();
            await Eventually.ThatAsync(() => !client.IsConnected);
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
            Assert.StartsWith("Get of item 0x0003 did not complete within", timeout.Message);
            Assert.IsAssignableFrom<IOException>(timeout.InnerException);           // the cause of the loss, not null
        }
    }

    [Fact]
    public async Task CommandTimeout_WhileQueuedBehindUnanswered()
    {
        var logs = new FakeLoggerFactory();
        var options = Resilient.Fast(logs);
        options.CommandTimeout = TimeSpan.FromMilliseconds(300);
        var (server, client) = await Resilient.StartAsync(options, s =>
        {
            s.OnRequest(ProductId.Code, _ => ControlReply.Item(new ProductId(7)).After(TimeSpan.FromMilliseconds(500)));
            s.Preload(new InterfaceVersion(529));
        });
        await using (server)
        await using (client)
        {
            using var cancelA = new CancellationTokenSource();
            var a = client.GetAsync<ProductId>(cancelA.Token);
            await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == ProductId.Code));
            cancelA.Cancel();                                                         // A's request stays on the line
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
            await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
            await Eventually.ThatAsync(() => logs.Events(1108).Count == 1);           // A's late reply settled the line
            Assert.Equal(529, (await client.GetAsync<InterfaceVersion>()).Version);
            Assert.Equal(1, server.Received.Count(r => r.Code == InterfaceVersion.Code));
        }
    }

    [Theory]
    [InlineData("before the call")]
    [InlineData("on admission")]
    [InlineData("on the line")]
    [InlineData("waiting for a reconnect")]
    public async Task Cancellation_BeforeWrite_NothingWritten(string where)
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(), s =>
        {
            s.Preload(new InterfaceVersion(529));
            s.OnRequest(ProductId.Code, _ => ControlReply.Item(new ProductId(7)).After(TimeSpan.FromMilliseconds(400)));
        });
        await using (server)
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            NetSdrControlClient? blocker = null;
            switch (where)
            {
                case "before the call":
                    cancel.Cancel();
                    break;
                case "on admission":                                                  // A holds admission until its late reply
                    _ = client.GetAsync<ProductId>();
                    await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == ProductId.Code));
                    break;
                case "on the line":                                                   // A was cancelled after its write and holds the line
                    using (var cancelA = new CancellationTokenSource())
                    {
                        var a = client.GetAsync<ProductId>(cancelA.Token);
                        await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == ProductId.Code));
                        cancelA.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
                    }

                    break;
                case "waiting for a reconnect":                                       // the server serves another client
                    blocker = new NetSdrControlClient();
                    await blocker.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
                    await server.DisconnectClientAsync();
                    await Eventually.ThatAsync(() => !client.IsConnected);
                    break;
            }

            var b = client.GetAsync<InterfaceVersion>(cancel.Token);
            if (!cancel.IsCancellationRequested)
            {
                await Task.Delay(100);
                cancel.Cancel();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(Limits.Test));
            if (blocker is not null) await blocker.DisposeAsync();
            await Task.Delay(600);                                                    // A's late reply or the reconnect has come
            Assert.DoesNotContain(server.Received, r => r.Code == InterfaceVersion.Code);
        }
    }

    [Fact]
    public async Task Cancellation_AfterWrite_ReturnsAtOnce()
    {
        var options = Resilient.Fast();
        options.ResponseTimeout = TimeSpan.FromSeconds(2);
        var (server, client) = await Resilient.StartAsync(options, s => s.OnRequest(ProductId.Code, _ => ControlReply.Silent));
        await using (server)
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            var call = client.GetAsync<ProductId>(cancel.Token);
            await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == ProductId.Code));
            var clock = Stopwatch.StartNew();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Limits.Test));
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500));
        }
    }

    [Fact]
    public async Task Cancellation_NextSetOfSameItem_GetsItsOwnEcho()
    {
        var logs = new FakeLoggerFactory();
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs),
            s => s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Echo.After(TimeSpan.FromMilliseconds(300)))));
        await using (server)
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            var first = client.SetAsync(new AfGain(0, 1), cancel.Token);
            await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == AfGain.Code));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(2, (await client.SetAsync(new AfGain(0, 2)).WaitAsync(Limits.Test)).Level);
            await Eventually.ThatAsync(() => logs.Events(1108).Count == 1);
            Assert.Equal(("CancelledCaller", "Reply"), (logs.Events(1108)[0].Value("Owner"), logs.Events(1108)[0].Value("Outcome")));
        }
    }

    [Fact]
    public async Task Stress_CommandsHeartbeatsRandomDisconnects()
    {
        var logs = new FakeLoggerFactory();
        var random = new Random(20261007);
        var (server, client) = await Resilient.StartAsync(Resilient.Fast(logs), s => s.OnRequest(AfGain.Code, _ =>
        {
            double roll;
            lock (random) roll = random.NextDouble();
            if (roll < 0.1)
            {
                _ = s.DisconnectClientAsync();
                return ControlReply.Silent;
            }

            return roll < 0.2 ? ControlReply.Echo.After(TimeSpan.FromMilliseconds(200)) : ControlReply.Echo;   // ResponseTimeout + 50 ms
        }));
        await using (server)
        await using (client)
        {
            var callers = Enumerable.Range(0, 4).Select(caller => Task.Run(async () =>
            {
                for (int i = 0; i < 50; i++)                                          // 200 calls over 4 callers
                {
                    byte level = (byte)(caller * 50 + i);
                    var call = client.SetAsync(new AfGain(0, level));
                    Assert.Same(call, await Task.WhenAny(call, Task.Delay(Limits.Test)));
                    try
                    {
                        Assert.Equal(level, (await call).Level);                      // never somebody else's echo
                    }
                    catch (Exception e) when (e is IOException or TimeoutException)
                    {
                        // Four attempts were not enough across the drops; the spec allows it.
                    }
                }
            })).ToArray();
            await Task.WhenAll(callers);
        }

        Assert.Empty(logs.Events(1106));
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientTimeoutTests"`
Expected: FAIL: `CommandTimeout_*` не отримують `TimeoutException` за 300 мс (`Limits.Test` спрацьовує на `WaitAsync`), `Cancellation_NextSetOfSameItem_GetsItsOwnEcho` не бачить 1108.

- [ ] **Step 3: Дедлайн і переклад винятків**

Спека 6.5 крок 1: `deadline = new CancellationTokenSource(CommandTimeout, tp)` (не створюється для `Infinite`), `token = linked(ct, deadline, _lifetime)`; дедлайн охоплює admission, усі спроби і очікування з'єднання, для команд і для запитів сесії. Крок 7: `OperationCanceledException` при скасованому `ct` летить як є; від дедлайну стає `TimeoutException($"{type} of item 0x{code:X4} did not complete within {CommandTimeout}.", exec.LastError ?? _lastLoss)`; від `_lifetime` стає `ClosedException()`. Крок 8 звільняє обидва CTS.

- [ ] **Step 4: Передача скасованого запиту**

Крок 4e: коли очікування `e.Request` скасовано токеном `t`, ставиться продовження на `e.Late.Task`, що пише 1108 з `Owner = CancelledCaller` і `Outcome` з результату, якщо обмін закінчився `Reply` або `Nak` (на `Lost` нічого); виняток летить далі без повтору. Запит лишається на дроті і тримає Wire, доки його не розв'яже `Settle` або спостерігач.

- [ ] **Step 5: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ResilientTimeoutTests"`
Expected: PASS (стрес триває близько 20-30 с: обрив на кожен десятий запит, підлога 1 с між спробами).
Потім `dotnet test NetSdr.sln`: усе зелене.

- [ ] **Step 6: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.cs NetSdr.Tests/Control/ResilientTimeoutTests.cs
git commit -m "feat: bound every resilient command by CommandTimeout and hand cancelled requests to the line" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
