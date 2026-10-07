# NetSdr Device Reboot and Recovery Policy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Навчити `ResilientControlClient` відновлювати пристрій, який перестав обслуговувати з'єднання: після кількох невдалих спроб перепідключення перезавантажити його окремим каналом (`IDeviceRebooter`) за рішенням `IRecoveryPolicy` (вбудована драбина `EscalatingRecoveryPolicy`: soft, потім hard), дочекатися завантаження і спробувати знову; дати застосунку ручне `RebootAsync(kind)` під керуванням наглядача; провести перше `ConnectAsync` через ту саму драбину з межею `ConnectAttempts`; показати все це на вигаданому сервісному протоколі Vega (`VegaRebooter`, емулятор) і дати `NetSdrTestServer` режими `Availability` і `ClearState()` для емуляції завислого і завантажуваного пристрою.

**Architecture:** Транспорт (`IDeviceRebooter`) і рішення (`IRecoveryPolicy`) розділені. Наглядач після кожної невдалої спроби, поки задано `Rebooter`, питає політику поза `_sync`; `Reboot(kind)` завершує поточну серію Polly внутрішнім `RebootScheduledException`, наглядач виконує спільний крок перезавантаження (1114, транспорт під `RebootTimeout`, лічильники, 1116, очікування завантаження на `TimeProvider`) і починає нову серію з паузами з 1 с, а сумарну межу `ReconnectAttempts` тримає власна перевірка перед кожною спробою. Ручний запит живе в одному слоті під `_sync` (`RebootRequest`: вид, `TaskCompletionSource`, прапорець виконання) і будить наглядача через `_rebootWake`, приєднаний до `WatchAsync`, до пауз Polly і до підлоги 1 с, але не до тіла спроби. Перше підключення ганяє ту саму спробу через окремий pipeline з 1118 без наглядача. Vega додає TCP-сервіс порту 50001, `VegaRebooter`, `VegaServiceServer` емулятора і поведінку завантаження через `ServerAvailability.CloseOnAccept`.

**Tech Stack:** .NET 10, C# latest, Polly.Core 8.8.0, Microsoft.Extensions.Logging.Abstractions 10.0.12 (`[LoggerMessage]`), xUnit 2.9.3, Microsoft.Extensions.TimeProvider.Testing 10.10.0 (`FakeTimeProvider`), Microsoft.Extensions.Diagnostics.Testing 10.10.0 (`FakeLogger`). Нових пакетів немає.

**Spec:** `docs/superpowers/specs/2026-10-07-netsdr-device-reboot-design.md` (далі "спека"; "спека 4.3" означає її розділ 4.3). Базова спека стійкості `2026-10-07-netsdr-resilience-logging-design.md` (далі "спека стійкості") описує код, який план розширює: 6 (внутрішня будова), 7 (наглядач), 8 (Polly), 10 (тести). Покрокові алгоритми спеки (4.2, 4.3, 4.5, 5.2-5.4) план не переписує, а посилається на них.

## Global Constraints

- Жодного нового `PackageReference` у жодному проєкті; `Directory.Build.props` не змінюється.
- EventId лише додаються: 1113 `RebootRequested` Information, 1114 `RebootEscalated` Warning, 1115 `RebootFailed` Warning, 1116 `RebootAccepted` Information, 1117 `RecoveryPolicyFailed` Warning, 1118 `ConnectAttemptFailed` Warning; шаблони дослівно з таблиці спеки 8, виняток іде аргументом `Exception`. 1106 отримує третю причину `recovery policy gave up`; наявні номери і шаблони не змінюються.
- Типові значення: `RebootTimeout` 10 с (додатний, не більше `int.MaxValue` мс), `ConnectAttempts` `null` (1 без `Rebooter`, 8 з `Rebooter`; явне значення не менше 1), `RecoveryPolicy` `null` з `Rebooter` означає `new EscalatingRecoveryPolicy()`; `EscalatingRecoveryPolicy`: `SoftRebootAfter` 3, `HardRebootAfter` 3, `MaxRebootsPerLoss` 2.
- `ReconnectPhase` переїжджає у власний файл і стає `public`; значення й порядок `Connect`, `Verify`, `Restore` без змін.
- Жодного типу Polly в public чи protected сигнатурі. `RebootScheduledException` і `RecoveryGaveUpException` приватні вкладені класи, назовні не виходять; нових публічних типів винятків немає.
- Нічого не логується під `_sync`; політика і транспорт ніколи не викликаються під `_sync`. Кожен виклик логера наглядача стоїть у `try`, як у спеці стійкості 3.1.
- Ідентифікатори, коментарі, XML-документація, тексти винятків і логів англійською; тексти, які фіксує спека (4.2 крок 3, 4.3 крок 2, 5.4, 6.2, 8), дослівно.
- Тести лише на loopback із портом 0 або на внутрішньому шві `PipeConnector`; кожне очікування обмежене `Limits.Test` (5 с), фальшивий час рухається через `AdvanceUntilAsync`.
- Перевірка задачі: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~<клас>"`; наприкінці кожної задачі `dotnet test NetSdr.sln --nologo -v q` зелений повністю (базова лінія 477 тестів: 448 `NetSdr.Tests` і 29 `NetSdr.Examples.Vega.Tests`, плюс нові). Кожна задача лишає рішення зібраним.
- Каталог `.claude/` не чіпати. Кожне повідомлення коміту закінчується рядком `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

П'ять ситуацій, які спека має на увазі, але жоден тест її розділу 10 не перевіряє, від найімовірнішої. Кожна має тест у задачі-власнику.

1. Ручний `RebootAsync` під час останньої дозволеної спроби: перезавантаження виконується, нова серія відхиляється перевіркою `ReconnectAttempts` ще до спроби, клієнт здається з причиною `attempts exhausted`, а задача викликача завершується винятком відмови, а не висить. Тест `RebootAsync_OnLastAllowedAttempt_RebootsThenGiveUpFailsTheCaller`, задача 7.
2. Транспорт ігнорує свій токен і не повертається: через `RebootTimeout` однаково 1115 з `TimeoutException`, а `DisposeAsync` під час такого виклику завершується в межах `Limits.Test`. Тест `RebooterIgnoresToken_TimeoutAndDisposeStillEnd`, задача 5.
3. Політика блокує потік (всупереч контракту): замок клієнта не тримається, тож `IsConnected`, `RebootAsync` (реєстрація) і команди не блокуються, а після повернення політики запит і команда виконуються. Тест `PolicyBlocks_NothingElseIsHeld`, задача 6.
4. `RebootAsync` з багатьох потоків одночасно на живому з'єднанні: рівно один виклик транспорту, усі задачі успішні. Тест `RebootAsync_ParallelCallers_OneReboot`, задача 6.
5. `GetBootTime` повертає понад `int.MaxValue` мс (наприклад `TimeSpan.MaxValue`): `Task.Delay` кинув би `ArgumentOutOfRangeException` і наглядач здався б з хибною причиною; трактується як від'ємне значення, тобто як невдача транспорту. Тест `BootTimeAboveInt32Milliseconds_TreatedAsFailure`, задача 5.

## File Structure

```
NetSdr/Control/
  RebootKind.cs                     новий: enum RebootKind { Soft, Hard } (1)
  IDeviceRebooter.cs                новий (1)
  RebootContext.cs                  новий (1)
  IRecoveryPolicy.cs                новий: IRecoveryPolicy, RecoveryContext, RecoveryAction, RecoveryActionKind (1)
  EscalatingRecoveryPolicy.cs       новий (1)
  ReconnectPhase.cs                 новий: enum переїжджає з ResilientControlClient.Log.cs і стає public (1)
  ResilientControlClientOptions.cs  Rebooter, RecoveryPolicy, RebootTimeout, ConnectAttempts (2); примітка CommandTimeout (11)
  ConnectionRestoredContext.cs      AfterReboot (2)
  ResilientControlClient.Log.cs     без ReconnectPhase (1); події 1113-1118 (2)
  ResilientControlClient.cs         перевірка опцій, _rebooter, _policy, _connectAttempts (2); ShouldHandle серії (5); перше підключення, _firstConnect (8); XML-документація ConnectAsync (11)
  ResilientControlClient.Types.cs   поля ReconnectState, RebootRequest (4)
  ResilientControlClient.Recovery.cs новий partial: RebootScheduledException, RecoveryGaveUpException, слот, DecideAfterFailure, RebootStepAsync (4, 5); RebootAsync (6)
  ResilientControlClient.Supervisor.cs   новий конструктор ConnectionRestoredContext (2); Cause стає nullable (4); серії і крок перезавантаження, пробудження WatchAsync, причина 1106 (5); слот у DisposeAsync і GiveUp (6)
NetSdr.Testing/
  ServerAvailability.cs             новий (3)
  NetSdrTestServer.cs               Availability, ClearState (3)
NetSdr.Tests/
  Control/EscalatingRecoveryPolicyTests.cs   новий (1)
  Control/ResilientConnectTests.cs           Options_Invalid, Options_Defaults (2)
  Control/ResilientFirstConnectTests.cs      новий: InvalidConnectAttempts_Throws (2); решта (8)
  Testing/TestServerAvailabilityTests.cs     новий (3)
  Control/FakeRebooter.cs                    новий: FakeRebooter, RecordingPolicy (4)
  Control/Resilient.cs                       WithRebooter (4)
  Control/ResilientRecoveryTests.cs          новий (4, 5)
  Control/ResilientRebootTests.cs            новий (6, 7)
examples/Vega/NetSdr.Examples.Vega/
  VegaProtocol.cs                   константи сервісного протоколу (9)
  VegaRebooter.cs                   новий: VegaRebooter, VegaRebooterOptions (9)
examples/Vega/NetSdr.Examples.Vega.Tests/
  VegaServiceServer.cs              новий (9)
  Items/VegaRebooterTests.cs        новий (9)
  VegaEmulator.cs                   сервісний сервер, Hang, перезавантаження (10)
  Receiver/VegaRecoveryTests.cs     новий (10)
docs/
  architecture.md                   компонент Resilience, таблиця потоків, діаграма Vega, динамічна діаграма 7 (11)
  superpowers/specs/2026-10-07-netsdr-device-reboot-design.md   статус (11)
```

Числа в дужках це задачі. Порядок: 1-4 додають типи, опції, тестовий сервер і помічники без зміни поведінки; 5-8 вбудовують драбину в наглядач, ручне перезавантаження і перше підключення; 9-10 Vega; 11 документи.

---

### Task 1: Публічні типи драбини і `ReconnectPhase`

**Files:**
- Create: `NetSdr/Control/RebootKind.cs`, `NetSdr/Control/IDeviceRebooter.cs`, `NetSdr/Control/RebootContext.cs`, `NetSdr/Control/IRecoveryPolicy.cs`, `NetSdr/Control/EscalatingRecoveryPolicy.cs`, `NetSdr/Control/ReconnectPhase.cs`
- Modify: `NetSdr/Control/ResilientControlClient.Log.cs`
- Test: `NetSdr.Tests/Control/EscalatingRecoveryPolicyTests.cs`

**Interfaces:**
- Produces (простір `NetSdr.Control`, усе за спекою 3):
  - `public enum RebootKind { Soft, Hard }`; `public enum RecoveryActionKind { Continue, Reboot, GiveUp }`; `public enum ReconnectPhase { Connect, Verify, Restore }` (той самий enum, що був internal у `ResilientControlClient.Log.cs`, з тією самою XML-документацією; з `Log.cs` він видаляється).
  - `public interface IDeviceRebooter { Task RebootAsync(RebootKind kind, RebootContext context, CancellationToken ct); TimeSpan GetBootTime(RebootKind kind); }` з XML-документацією спеки дослівно.
  - `public sealed class RebootContext { public RebootContext(string target, IPEndPoint? lastRemoteEndPoint, bool requested); public string Target { get; } public IPEndPoint? LastRemoteEndPoint { get; } public bool Requested { get; } }` (`target` `null` дає `ArgumentNullException`).
  - `public interface IRecoveryPolicy { RecoveryAction OnAttemptFailed(RecoveryContext context); }`.
  - `public readonly record struct RecoveryContext(int FailedAttempts, int FailedAttemptsSinceReboot, ReconnectPhase Phase, Exception Failure, TimeSpan Downtime, int SoftReboots, int HardReboots);`.
  - `public readonly struct RecoveryAction : IEquatable<RecoveryAction> { public static RecoveryAction Continue { get; } public static RecoveryAction GiveUp { get; } public static RecoveryAction Reboot(RebootKind kind); public RecoveryActionKind Kind { get; } public RebootKind RebootKind { get; } }` плюс `Equals(object?)`, `GetHashCode`, `==`, `!=`, `ToString()` (`"Continue"`, `"GiveUp"`, `"Reboot(Soft)"`); рівність за обома полями.
  - `public sealed class EscalatingRecoveryPolicy : IRecoveryPolicy { public int SoftRebootAfter { get; init; } = 3; public int HardRebootAfter { get; init; } = 3; public int MaxRebootsPerLoss { get; init; } = 2; public RecoveryAction OnAttemptFailed(RecoveryContext context); }`; `init` кидає `ArgumentOutOfRangeException(nameof(value), value, "SoftRebootAfter must be at least 1.")` (відповідно `"HardRebootAfter must be at least 1."`, `"MaxRebootsPerLoss must not be negative."`).

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class EscalatingRecoveryPolicyTests
{
    static RecoveryContext Ctx(int k, int soft = 0, int hard = 0) =>
        new(k + soft + hard, k, ReconnectPhase.Connect, new IOException("x"), TimeSpan.FromSeconds(k), soft, hard);

    [Fact]
    public void Defaults_ContinueBeforeSoft_SoftAtThree()
    {
        var p = new EscalatingRecoveryPolicy();
        Assert.Equal((3, 3, 2), (p.SoftRebootAfter, p.HardRebootAfter, p.MaxRebootsPerLoss));
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(1)));
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(2)));
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Soft), p.OnAttemptFailed(Ctx(3)));
    }

    [Fact]
    public void AfterSoft_HardAtThree()
    {
        var p = new EscalatingRecoveryPolicy();
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(1, soft: 1)));
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(2, soft: 1)));
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Hard), p.OnAttemptFailed(Ctx(3, soft: 1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(50)]
    public void AfterMaxReboots_AlwaysContinue(int k) =>
        Assert.Equal(RecoveryAction.Continue, new EscalatingRecoveryPolicy().OnAttemptFailed(Ctx(k, soft: 1, hard: 1)));

    [Fact]
    public void MaxRebootsZero_NeverReboots() =>
        Assert.Equal(RecoveryAction.Continue, new EscalatingRecoveryPolicy { MaxRebootsPerLoss = 0 }.OnAttemptFailed(Ctx(30)));

    [Fact]
    public void SoftRebootAfterOne_FirstFailureReboots() =>
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Soft), new EscalatingRecoveryPolicy { SoftRebootAfter = 1 }.OnAttemptFailed(Ctx(1)));

    [Fact]
    public void ManualHardFirst_NextIsHard() =>
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Hard),
            new EscalatingRecoveryPolicy { MaxRebootsPerLoss = 3 }.OnAttemptFailed(Ctx(3, soft: 0, hard: 1)));

    [Theory]
    [InlineData("SoftRebootAfter", 0)]
    [InlineData("HardRebootAfter", 0)]
    [InlineData("MaxRebootsPerLoss", -1)]
    public void InvalidValues_Throw(string property, int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _ = property switch
        {
            "SoftRebootAfter" => new EscalatingRecoveryPolicy { SoftRebootAfter = value },
            "HardRebootAfter" => new EscalatingRecoveryPolicy { HardRebootAfter = value },
            _ => new EscalatingRecoveryPolicy { MaxRebootsPerLoss = value },
        });
        Assert.StartsWith(property, ex.Message);
    }

    [Fact]
    public void RecoveryAction_Equality()
    {
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Soft), RecoveryAction.Reboot(RebootKind.Soft));
        Assert.NotEqual(RecoveryAction.Reboot(RebootKind.Soft), RecoveryAction.Reboot(RebootKind.Hard));
        Assert.NotEqual(RecoveryAction.Reboot(RebootKind.Soft), RecoveryAction.Continue);
        Assert.True(RecoveryAction.GiveUp == RecoveryAction.GiveUp && RecoveryAction.GiveUp != RecoveryAction.Continue);
        Assert.Equal((RecoveryActionKind.Reboot, RebootKind.Hard), (RecoveryAction.Reboot(RebootKind.Hard).Kind, RecoveryAction.Reboot(RebootKind.Hard).RebootKind));
        Assert.Equal("Reboot(Soft)", RecoveryAction.Reboot(RebootKind.Soft).ToString());
    }

    [Fact]
    public void RebootContext_Fields()
    {
        var ctx = new RebootContext("host:50000", new IPEndPoint(IPAddress.Loopback, 50000), requested: true);
        Assert.Equal(("host:50000", 50000, true), (ctx.Target, ctx.LastRemoteEndPoint!.Port, ctx.Requested));
        Assert.Throws<ArgumentNullException>(() => new RebootContext(null!, null, false));
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~EscalatingRecoveryPolicyTests"`
Expected: FAIL: збірка `NetSdr.Tests` не компілюється, типів `EscalatingRecoveryPolicy`, `RecoveryAction`, `RebootContext` немає.

- [ ] **Step 3: Типи**

Шість файлів за **Interfaces**. `ReconnectPhase` переноситься з `ResilientControlClient.Log.cs` без змін тексту документації, крім `internal` на `public`. `EscalatingRecoveryPolicy.OnAttemptFailed` дослівно за таблицею спеки 3.1 (`n = SoftReboots + HardReboots`, `k = FailedAttemptsSinceReboot`; порядок рядків важливий: межа `n >= MaxRebootsPerLoss` перевіряється першою). XML-документація кожного публічного члена англійською, тексти спеки 3 дослівно; документація `IRecoveryPolicy.OnAttemptFailed` і `IDeviceRebooter` саме ті, що в спеці.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~EscalatingRecoveryPolicyTests"`
Expected: PASS (13 тестів). Потім `dotnet test NetSdr.sln --nologo -v q`: 490 зелених.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/RebootKind.cs NetSdr/Control/IDeviceRebooter.cs NetSdr/Control/RebootContext.cs NetSdr/Control/IRecoveryPolicy.cs NetSdr/Control/EscalatingRecoveryPolicy.cs NetSdr/Control/ReconnectPhase.cs NetSdr/Control/ResilientControlClient.Log.cs NetSdr.Tests/Control/EscalatingRecoveryPolicyTests.cs
git commit -m "feat: add IDeviceRebooter, IRecoveryPolicy and the escalating soft-then-hard policy" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Опції, `AfterReboot` і події 1113-1118

**Files:**
- Modify: `NetSdr/Control/ResilientControlClientOptions.cs`, `NetSdr/Control/ConnectionRestoredContext.cs`, `NetSdr/Control/ResilientControlClient.Log.cs`, `NetSdr/Control/ResilientControlClient.cs` (`Validated`, конструктор)
- Modify: `NetSdr/Control/ResilientControlClient.Supervisor.cs` (`RestoreAsync` викликає новий конструктор `ConnectionRestoredContext`)
- Test: `NetSdr.Tests/Control/ResilientConnectTests.cs`, create `NetSdr.Tests/Control/ResilientFirstConnectTests.cs`

**Interfaces:**
- Consumes: `Validated`, `RequireFinite` (спека стійкості, задача 6 попереднього плану); `ResilientClientLog`.
- Produces:
  - `ResilientControlClientOptions`: `public IDeviceRebooter? Rebooter { get; set; }`, `public IRecoveryPolicy? RecoveryPolicy { get; set; }`, `public TimeSpan RebootTimeout { get; set; } = TimeSpan.FromSeconds(10);`, `public int? ConnectAttempts { get; set; }`; XML-документація з правил спеки 3.2 (без `Rebooter` політика не викликається; `null` з `Rebooter` означає драбину; автоматичне 8 не залежить від чисел політики).
  - `ConnectionRestoredContext`: `public RebootKind? AfterReboot { get; }`; конструктор `internal ConnectionRestoredContext(INetSdrControlClient client, Exception cause, DateTimeOffset lostAt, RebootKind? afterReboot)`; `RestoreAsync` поки передає `null` (задача 5 передасть `state.AfterReboot`).
  - `ResilientControlClient`: поля `private readonly IDeviceRebooter? _rebooter; private readonly IRecoveryPolicy? _policy; private readonly int _connectAttempts;`, у конструкторі `_rebooter = options.Rebooter; _policy = _rebooter is null ? null : options.RecoveryPolicy ?? new EscalatingRecoveryPolicy(); _connectAttempts = options.ConnectAttempts ?? (_rebooter is null ? 1 : 8);`. `Validated` копіює чотири нові опції, перевіряє `RequireFinite(options.RebootTimeout, nameof(options.RebootTimeout), nameof(options))` і `ConnectAttempts is < 1` дає `ArgumentOutOfRangeException(nameof(options), value, "ConnectAttempts must be at least 1.")`.
  - `ResilientClientLog`, усі `public static partial void`, перший параметр `ILogger logger`: 1113 `RebootRequested(RebootKind kind, string target)`; 1114 `RebootEscalated(RebootKind kind, string target, int failedAttempts, ReconnectPhase phase, Exception exception)`; 1115 `RebootFailed(RebootKind kind, string target, Exception exception)`; 1116 `RebootAccepted(string target, RebootKind kind, TimeSpan bootTime)`; 1117 `RecoveryPolicyFailed(Exception exception)`; 1118 `ConnectAttemptFailed(int attempt, int attempts, string target, ReconnectPhase phase, TimeSpan delay, Exception exception)`. Рівні й шаблони з таблиці спеки 8.

- [ ] **Step 1: Написати тести, що падають**

У `ResilientConnectTests.Invalid` додати `["RebootTimeout 0"] = o => o.RebootTimeout = TimeSpan.Zero`, `["RebootTimeout Infinite"] = o => o.RebootTimeout = Timeout.InfiniteTimeSpan`; в `Options_Defaults` додати `Assert.Null(o.Rebooter); Assert.Null(o.RecoveryPolicy); Assert.Null(o.ConnectAttempts); Assert.Equal(10, (int)o.RebootTimeout.TotalSeconds);`. Новий файл:

```csharp
public class ResilientFirstConnectTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidConnectAttempts_Throws(int attempts)
    {
        var options = new ResilientControlClientOptions { ConnectAttempts = attempts };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => { _ = ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 1), options); });
        Assert.Contains("ConnectAttempts must be at least 1.", ex.Message);
    }

    [Fact]
    public async Task RecoveryPolicyWithoutRebooter_IsAllowed()
    {
        var options = new ResilientControlClientOptions { RecoveryPolicy = new EscalatingRecoveryPolicy() };   // validated, never called
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThrowsAsync<SocketException>(() => ResilientControlClient.ConnectAsync(refused.ConnectAsync, "pipe", options, default).WaitAsync(Limits.Test));
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientConnectTests|FullyQualifiedName~ResilientFirstConnectTests"`
Expected: FAIL: не компілюється, властивостей `RebootTimeout`, `ConnectAttempts`, `RecoveryPolicy` немає.

- [ ] **Step 3: Опції, контекст, події**

За **Interfaces**. Документація `RebootTimeout`: межа одного виклику `IDeviceRebooter.RebootAsync`, після неї транспорт вважається невдалим (`TimeoutException`), наступні спроби тривають. Документація `ConnectAttempts`: кількість спроб `ConnectAsync`, паузи 1, 2, 4 ... 30 с, `null` означає 1 без `Rebooter` і 8 з ним; хто міняє числа `EscalatingRecoveryPolicy`, задає явно. `ReconnectAttempts` отримує речення: межа діє на всю втрату крізь перезавантаження. Поля `_rebooter`, `_policy`, `_connectAttempts` читають задачі 5 і 8; до того вони лише присвоюються (присвоєне поле попередження не дає).

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientConnectTests|FullyQualifiedName~ResilientFirstConnectTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/ResilientControlClientOptions.cs NetSdr/Control/ConnectionRestoredContext.cs NetSdr/Control/ResilientControlClient.Log.cs NetSdr/Control/ResilientControlClient.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr.Tests/Control/ResilientConnectTests.cs NetSdr.Tests/Control/ResilientFirstConnectTests.cs
git commit -m "feat: reboot and recovery options, AfterReboot on the restore context, log events 1113-1118" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `NetSdrTestServer.Availability` і `ClearState`

**Files:**
- Create: `NetSdr.Testing/ServerAvailability.cs`
- Modify: `NetSdr.Testing/NetSdrTestServer.cs`
- Test: `NetSdr.Tests/Testing/TestServerAvailabilityTests.cs`

**Interfaces:**
- Consumes: `AcceptLoopAsync`, `HandleFrameAsync`, `_state`, `_sync`.
- Produces:
  - `public enum ServerAvailability { Normal, CloseOnAccept, Silent }` (простір `NetSdr.Testing`), документація з правил спеки 7.
  - `NetSdrTestServer`: `public ServerAvailability Availability { get; set; }` (поле `volatile`, читається без замка), `public void ClearState()` (під `_sync` очищає `_state`; обробники і `Received` лишаються).

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class TestServerAvailabilityTests
{
    static NetSdrControlClientOptions Quick() => new() { ResponseTimeout = TimeSpan.FromMilliseconds(200), FaultOnTimeout = false };

    [Fact]
    public async Task CloseOnAccept_NewConnectionClosed_CurrentKept()
    {
        var (server, client) = await Loopback.StartAsync();
        await using var _ = server; await using var __ = client;
        server.Availability = ServerAvailability.CloseOnAccept;
        Assert.Equal(7, (await client.SetAsync(new AfGain(0, 7)).WaitAsync(Limits.Test)).Level);     // the current client is served
        await client.DisposeAsync();                                                                 // the server moves on to the next socket
        await using var next = new NetSdrControlClient();
        await next.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)).WaitAsync(Limits.Test);
        await Assert.ThrowsAnyAsync<Exception>(() => next.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));   // closed before it was read
        await Eventually.ThatAsync(() => !next.IsConnected);
        Assert.DoesNotContain(server.Received, r => r.Code == InterfaceVersion.Code);
    }

    [Fact]
    public async Task Silent_RequestsRecorded_NoReplies()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)), Quick());
        await using var _ = server; await using var __ = client;
        server.Availability = ServerAvailability.Silent;                                             // acts on the current connection
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
        Assert.Equal(InterfaceVersion.Code, Assert.Single(server.Received).Code);
        await server.SendUnsolicitedAsync(new AfGain(0, 1));                                         // explicit frames still go out
        Assert.Equal(1, (await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).As<AfGain>().Level);
    }

    [Fact]
    public async Task BackToNormal_ServesAgain()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)), Quick());
        await using var _ = server; await using var __ = client;
        Assert.Equal(ServerAvailability.Normal, server.Availability);
        server.Availability = ServerAvailability.Silent;
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
        server.Availability = ServerAvailability.Normal;
        Assert.Equal(529, (await client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test)).Version);
    }

    [Fact]
    public async Task ClearState_RemovesPreloads()
    {
        var (server, client) = await Loopback.StartAsync(s => s.Preload(new InterfaceVersion(529)));
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new AfGain(0, 7));
        server.ClearState();
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<InterfaceVersion>().WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<AfGain, byte>(0).WaitAsync(Limits.Test));
        Assert.Equal(3, server.Received.Count);                                                      // Received is kept
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~TestServerAvailabilityTests"`
Expected: FAIL: не компілюється, `ServerAvailability` і `Availability` немає.

- [ ] **Step 3: Реалізація**

`AcceptLoopAsync` після `AcceptSocketAsync`: при `Availability == CloseOnAccept` викликати `socket.Dispose()` і продовжити цикл, не торкаючись `_client` і не чекаючи. `HandleFrameAsync`: одразу після `_received.Add(request)` при `Availability == Silent` повернутися, не викликаючи `DispatchAsync`: обробник не виконується, стан не змінюється, нічого не пишеться (`SendUnsolicitedAsync` і потік UDP ідуть своїми шляхами). Слухач ніколи не зупиняється. `ClearState` лише `_state.Clear()` під `_sync`. XML-документація `Availability` описує три режими словами спеки 7.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~TestServerAvailabilityTests|FullyQualifiedName~TestServerControlTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add NetSdr.Testing/ServerAvailability.cs NetSdr.Testing/NetSdrTestServer.cs NetSdr.Tests/Testing/TestServerAvailabilityTests.cs
git commit -m "feat: test server availability modes CloseOnAccept and Silent, and ClearState" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Помічники тестів `FakeRebooter`, `RecordingPolicy` і стан втрати

**Files:**
- Create: `NetSdr.Tests/Control/FakeRebooter.cs`, `NetSdr/Control/ResilientControlClient.Recovery.cs`
- Modify: `NetSdr/Control/ResilientControlClient.Types.cs`, `NetSdr/Control/ResilientControlClient.Supervisor.cs` (`state.Cause!` у `RestoreAsync`), `NetSdr.Tests/Control/Resilient.cs`
- Test: `NetSdr.Tests/Control/ResilientRecoveryTests.cs` (створюється тут, решта тестів у задачі 5)

**Interfaces:**
- Consumes: `ReconnectState`, `PipeConnector`, `Resilient.Seam`, `AdvanceUntilAsync`.
- Produces:
  - `ReconnectState` (спека 5.1): конструктор `ReconnectState(Exception? cause, DateTimeOffset lostAt, long lostTimestamp)` і `public Exception? Cause { get; }` (`null` лише для першого підключення, задача 8); нові `public int FailedAttemptsSinceReboot { get; set; } public int SoftReboots { get; set; } public int HardReboots { get; set; } public RebootKind? AfterReboot { get; set; } public Exception? LastFailure { get; set; } public List<RebootRequest> ManualWaiters { get; } = [];`.
  - У `Recovery.cs` (partial `ResilientControlClient`): `private sealed class RebootRequest { public RebootKind Kind { get; set; } public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool Running { get; set; } }`; `private sealed class RebootScheduledException(RebootKind kind, Exception failure) : Exception("A reboot was scheduled.", failure) { public RebootKind Kind { get; } = kind; }`; `private sealed class RecoveryGaveUpException(Exception failure) : Exception("The recovery policy gave up.", failure);`; поля `private RebootRequest? _rebootRequest; private CancellationTokenSource _rebootWake = new();` (обидва під `_sync`).
  - Тести: `internal sealed class FakeRebooter : IDeviceRebooter { public ConcurrentQueue<RebootKind> Calls { get; } public ConcurrentQueue<RebootContext> Contexts { get; } public TimeSpan BootTime { get; set; } = TimeSpan.FromSeconds(2); public Func<RebootKind, TimeSpan>? BootTimeOf { get; set; } public Func<RebootKind, RebootContext, CancellationToken, Task>? OnReboot { get; set; } }` (`RebootAsync` записує виклик і контекст, повертає `OnReboot?.Invoke(kind, context, ct) ?? Task.CompletedTask`; `GetBootTime` повертає `BootTimeOf?.Invoke(kind) ?? BootTime`) і `public static FakeRebooter Booting(NetSdrTestServer server, TimeSpan bootTime)`: `OnReboot` ставить `server.Availability = ServerAvailability.CloseOnAccept` і через `bootTime` реального часу (`Task.Delay`) повертає `Normal`; `BootTime = bootTime + TimeSpan.FromMilliseconds(100)`. `internal sealed class RecordingPolicy : IRecoveryPolicy { public ConcurrentQueue<RecoveryContext> Calls { get; } public Func<RecoveryContext, RecoveryAction> Decide { get; set; } = _ => RecoveryAction.Continue; }` (`OnAttemptFailed` записує й повертає `Decide(context)`). У `Resilient`: `public static ResilientControlClientOptions WithRebooter(this ResilientControlClientOptions options, IDeviceRebooter rebooter, IRecoveryPolicy? policy = null)` ставить обидві опції і повертає `options`.

- [ ] **Step 1: Написати тести**

```csharp
public class ResilientRecoveryTests
{
    // A seam that accepts attempt 1 and refuses every later one until `back` says otherwise.
    static PipeConnector Failing(FakeTimeProvider time, Func<bool>? back = null) => new(time)
    {
        Before = (n, _) => n == 1 || back?.Invoke() == true ? Task.CompletedTask : Resilient.Refused(),
        Serve = d => d.NakEverythingAsync(),
    };

    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task NoRebooter_PolicyNeverCalled()
    {
        var (logs, time, policy) = (new FakeLoggerFactory(), new FakeTimeProvider(), new RecordingPolicy());
        var connector = Failing(time);
        var options = Resilient.Seam(logs, time);
        options.RecoveryPolicy = policy;                                   // allowed without a Rebooter, never called
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            device.CloseRemote();
            await time.AdvanceUntilAsync(() => logs.Events(1104).Count >= 5, TimeSpan.FromMilliseconds(500));
            Assert.Empty(policy.Calls);
        }
    }

    [Fact]
    public async Task FakeRebooter_RecordsCallsAndBootTime()
    {
        var rebooter = new FakeRebooter { BootTimeOf = k => k == RebootKind.Hard ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(1) };
        await rebooter.RebootAsync(RebootKind.Hard, new RebootContext("pipe", null, false), default);
        Assert.Equal(new[] { RebootKind.Hard }, rebooter.Calls);
        Assert.Equal((4, 1), ((int)rebooter.GetBootTime(RebootKind.Hard).TotalSeconds, (int)rebooter.GetBootTime(RebootKind.Soft).TotalSeconds));
        Assert.False(Assert.Single(rebooter.Contexts).Requested);
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRecoveryTests"`
Expected: FAIL: не компілюється, `FakeRebooter` і `RecordingPolicy` немає. `NoRebooter_PolicyNeverCalled` проходить одразу після компіляції: він охороняє задачі 5-8.

- [ ] **Step 3: Помічники і стан**

За **Interfaces**. `Recovery.cs` поки містить лише типи й поля: логіку слоту додає задача 5, `RebootAsync` задача 6. `SuperviseAsync` і `ReconnectOnceAsync` не змінюються.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRecoveryTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add NetSdr.Tests/Control/FakeRebooter.cs NetSdr/Control/ResilientControlClient.Recovery.cs NetSdr/Control/ResilientControlClient.Types.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr.Tests/Control/Resilient.cs NetSdr.Tests/Control/ResilientRecoveryTests.cs
git commit -m "test: FakeRebooter and RecordingPolicy, and the per-loss reboot counters on ReconnectState" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Ескалація в циклі перепідключення

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.Recovery.cs`, `NetSdr/Control/ResilientControlClient.Supervisor.cs`, `NetSdr/Control/ResilientControlClient.cs` (`ShouldHandle` і `OnRetry` pipeline перепідключення)
- Test: `NetSdr.Tests/Control/ResilientRecoveryTests.cs`

**Interfaces:**
- Consumes: `_rebooter`, `_policy` (2); `ReconnectState`, `RebootRequest`, `RebootScheduledException`, `RecoveryGaveUpException`, `_rebootRequest`, `_rebootWake` (4); `SuperviseAsync`, `WatchAsync`, `HeartbeatTurnAsync`, `ReconnectOnceAsync`, `GiveUp`, `OpenLinkAsync`, `RestoreAsync`, `ReconnectKey`, `AttemptFloor`.
- Produces (усе в `Recovery.cs`, якщо не сказано інакше):
  - `private RebootRequest? TakeRebootRequest()`: під `_sync` бере `_rebootRequest` (якщо є, `Running = true`; запит лишається в слоті, щоб пізніші виклики приєднувалися), і завжди підміняє `_rebootWake` новим `CancellationTokenSource`, старий звільняє поза замком. Повертає запит або `null`.
  - `private CancellationToken CurrentWake()`: `_rebootWake.Token` під `_sync`.
  - `private void ClearRebootRequest(RebootRequest request)`: під `_sync` `_rebootRequest = null`, якщо це той самий об'єкт.
  - `private Exception DecideAfterFailure(ReconnectState state, Exception failure, bool checkSlot)` (спека 5.3): записує `state.LastFailure = failure`; якщо `_policy is null`, повертає `failure`; при `checkSlot` і запиті в слоті повертає `new RebootScheduledException(request.Kind, failure)` без політики; інакше `state.FailedAttemptsSinceReboot++`, `policy.OnAttemptFailed(new RecoveryContext(state.Attempt, state.FailedAttemptsSinceReboot, state.Phase, failure, _time.GetElapsedTime(state.LostTimestamp), state.SoftReboots, state.HardReboots))` у `try`; виняток політики дає 1117 (у власному `try`) і `failure`; `Reboot(kind)` дає `RebootScheduledException`, `GiveUp` дає `RecoveryGaveUpException(failure)`, `Continue` дає `failure`.
  - `private async Task RebootStepAsync(RebootKind kind, ReconnectState state, RebootRequest? request, Exception? lastFailure, CancellationToken ct)` (спека 4.3; `ct` це токен життя, а для першого підключення токен викликача).
  - `private async Task<Link> RecoverAsync(ReconnectState state)` у `.Supervisor.cs`: серії спроб однієї втрати з кроками перезавантаження між ними; повертає перевірений Link або кидає (відмова, закриття).
  - `WatchAsync(Link link, CancellationToken wake)` і `HeartbeatTurnAsync(Link link, Task completion, CancellationToken wake)`: повертаються також тоді, коли `wake` скасовано; скасування `wake` не є збоєм heartbeat.
  - `GiveUp(Exception ex, int attempts, bool watching)` розпізнає `RecoveryGaveUpException`: причина `recovery policy gave up`, `cause = ex.InnerException`, повідомлення `IOException` те саме, що для вичерпаних спроб.

- [ ] **Step 1: Написати тести, що падають**

Додати до `ResilientRecoveryTests` (усі на `Resilient.Seam(logs, time).WithRebooter(rebooter, policy)`, `Failing(time)`, втрата через `time.Advance(1 s); device.CloseRemote()` як у `NoRebooter_PolicyNeverCalled`; `Start` нижче це саме цей пролог, повертає `(client, device, connector)`):

```csharp
    [Fact]
    public async Task Escalation_SoftAfterThree_BootWait_BackoffRestarts()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, device, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            DateTimeOffset lost = time.GetUtcNow();
            await time.AdvanceUntilAsync(() => connector.Attempts >= 7, Step);
            double[] starts = connector.AttemptTimes.Skip(1).Take(6).Select(t => (t - lost).TotalSeconds).ToArray();
            double[] expected = [0, 1, 3, 5, 6, 8];                           // soft at 3 s, bootTime 2 s, then 1, 2 s again
            for (int i = 0; i < 6; i++) Assert.InRange(starts[i], expected[i], expected[i] + 0.3);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            var escalated = Assert.Single(logs.Events(1114));
            Assert.Equal((LogLevel.Warning, "Soft", "3", "Connect"), (escalated.Level, escalated.Value("Kind"), escalated.Value("FailedAttempts"), escalated.Value("Phase")));
            Assert.IsType<SocketException>(escalated.Exception);
            Assert.Equal((LogLevel.Information, "pipe", TimeSpan.FromSeconds(2)), (logs.Events(1116)[0].Level, logs.Events(1116)[0].Value("Target"), logs.Events(1116)[0].Span("BootTime")));
            Assert.Equal(new[] { 1.0, 2, 1, 2 }, logs.Events(1104).Take(4).Select(r => r.Span("Delay").TotalSeconds));
            Assert.False(Assert.Single(rebooter.Contexts).Requested);
        }
    }

    [Fact]
    public async Task Escalation_HardWhenSoftDidNotHelp()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => connector.Attempts >= 12, Step);
            Assert.Equal(new[] { RebootKind.Soft, RebootKind.Hard }, rebooter.Calls);  // after hard only plain attempts
            Assert.Equal(new[] { "Soft", "Hard" }, logs.Events(1114).Select(r => r.Value("Kind")));
        }
    }

    [Fact]
    public async Task Escalation_SucceedsAfterSoft_RestoredSeesAfterReboot()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        RebootKind? seen = null;
        var (client, _, connector) = await Start(logs, time, rebooter, back: () => rebooter.Calls.Count > 0,
            restore: (ctx, _) => { seen = ctx.AfterReboot; return Task.CompletedTask; });
        await using (client)
        {
            await time.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, Step);
            Assert.Equal(RebootKind.Soft, seen);
            Assert.Equal("4", logs.Events(1105)[0].Value("Attempts"));
            Assert.True(client.IsConnected);
        }
    }

    [Fact]
    public async Task RebooterThrows_1115_CountedAndContinues()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (k, _, _) => k == RebootKind.Soft ? throw new ApplicationException("no service") : Task.CompletedTask;
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2, Step);
            Assert.Equal(new[] { RebootKind.Soft, RebootKind.Hard }, rebooter.Calls);
            Assert.IsType<ApplicationException>(Assert.Single(logs.Events(1115)).Exception);
            Assert.Equal("Hard", Assert.Single(logs.Events(1116)).Value("Kind"));   // no 1116 and no boot wait for the failed soft
            Assert.InRange((connector.AttemptTimes.ElementAt(4) - connector.AttemptTimes.ElementAt(3)).TotalSeconds, 1, 1.3);   // attempt 4 right after the floor
        }
    }

    [Fact]
    public async Task RebooterTimeout_TimeoutException()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (_, _, ct) => Task.Delay(Timeout.Infinite, ct);
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.RebootTimeout = TimeSpan.FromMilliseconds(500));
        await using (client)
        {
            await time.AdvanceUntilAsync(() => logs.Events(1115).Count == 1, Step);
            var timeout = Assert.IsType<TimeoutException>(logs.Events(1115)[0].Exception);
            Assert.StartsWith("The Soft reboot of pipe did not complete within", timeout.Message);
            await time.AdvanceUntilAsync(() => connector.Attempts >= 5, Step);        // attempts go on
        }
    }

    // Review Focus 2.
    [Fact]
    public async Task RebooterIgnoresToken_TimeoutAndDisposeStillEnd()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.OnReboot = (_, _, _) => new TaskCompletionSource().Task;            // never completes, ignores ct
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.RebootTimeout = TimeSpan.FromMilliseconds(500));
        await time.AdvanceUntilAsync(() => logs.Events(1115).Count == 1, Step);
        Assert.IsType<TimeoutException>(logs.Events(1115)[0].Exception);
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2, Step);        // the hard reboot hangs now
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);
        Assert.True(client.Completion.IsCompletedSuccessfully);
        Assert.Empty(logs.Events(1106));
    }

    [Fact]
    public Task BootTimeNegative_TreatedAsFailure() => BootTimeIsFailureAsync(-1);

    // Review Focus 5.
    [Fact]
    public Task BootTimeAboveInt32Milliseconds_TreatedAsFailure() => BootTimeIsFailureAsync(int.MaxValue + 1L);

    async Task BootTimeIsFailureAsync(long milliseconds)
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        rebooter.BootTimeOf = _ => TimeSpan.FromMilliseconds(milliseconds);
        var (client, _, connector) = await Start(logs, time, rebooter);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 2, Step);
            Assert.Equal(2, logs.Events(1115).Count);
            Assert.All(logs.Events(1115), r => Assert.IsType<InvalidOperationException>(r.Exception));
            Assert.Empty(logs.Events(1116));
            Assert.Empty(logs.Events(1106));
        }
    }

    [Fact]
    public async Task PolicyThrows_1117_Continue()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = _ => throw new ApplicationException("policy") };
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => connector.Attempts >= 5, Step);
            Assert.Equal(4, logs.Events(1117).Count);
            Assert.All(logs.Events(1117), r => Assert.Equal(LogLevel.Warning, r.Level));
            Assert.Empty(rebooter.Calls);
            Assert.Equal(new[] { 1.0, 2, 4 }, logs.Events(1104).Take(3).Select(r => r.Span("Delay").TotalSeconds));
        }
    }

    [Fact]
    public async Task PolicyGivesUp_1106_CompletionFaults()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = c => c.FailedAttempts == 2 ? RecoveryAction.GiveUp : RecoveryAction.Continue };
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, Step);
        var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.IsType<SocketException>(failure.InnerException);
        Assert.Contains("after 2 attempt(s)", failure.Message);
        Assert.Equal((LogLevel.Error, "recovery policy gave up", "2"), (logs.Events(1106)[0].Level, logs.Events(1106)[0].Value("Reason"), logs.Events(1106)[0].Value("Attempts")));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task RecoveryContext_Fields()
    {
        // Connect: the seam refuses. Downtime grows with fake time, the counters start at zero.
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy();
        var (client, _, connector) = await Start(logs, time, rebooter, policy);
        await using (client)
        {
            await time.AdvanceUntilAsync(() => policy.Calls.Count >= 4, Step);
            var calls = policy.Calls.ToArray();
            Assert.Equal((1, 1, ReconnectPhase.Connect, 0, 0), (calls[0].FailedAttempts, calls[0].FailedAttemptsSinceReboot, calls[0].Phase, calls[0].SoftReboots, calls[0].HardReboots));
            Assert.IsType<SocketException>(calls[0].Failure);
            Assert.True(calls[3].Downtime > calls[0].Downtime && calls[3].Downtime >= TimeSpan.FromSeconds(7));
            Assert.Equal((4, 4), (calls[3].FailedAttempts, calls[3].FailedAttemptsSinceReboot));
        }

        // Verify: a device that accepts and never answers. Restore: a callback that throws.
        foreach (var (phase, serve, restore) in new (ReconnectPhase, Func<PipeDevice, Task>, Func<ConnectionRestoredContext, CancellationToken, Task>?)[]
        {
            (ReconnectPhase.Verify, _ => Task.CompletedTask, null),
            (ReconnectPhase.Restore, d => d.NakEverythingAsync(), (_, _) => throw new ApplicationException("restore")),
        })
        {
            var (l, t, r, p) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter(), new RecordingPolicy());
            var connector2 = new PipeConnector(t) { Serve = serve };
            var options = Resilient.Seam(l, t).WithRebooter(r, p);
            options.ConnectionRestored = restore;
            var (c, d) = await connector2.StartAsync(options);     // StartAsync answers the first verification itself when Serve is null; here Serve is set
            await using (c)
            {
                t.Advance(TimeSpan.FromSeconds(1));
                d.CloseRemote();
                await t.AdvanceUntilAsync(() => p.Calls.Count >= 1, Step);
                Assert.Equal(phase, p.Calls.First().Phase);
            }
        }
    }

    [Fact]
    public async Task CountersResetForNewLoss()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var policy = new RecordingPolicy { Decide = new EscalatingRecoveryPolicy().OnAttemptFailed };
        bool back = false;
        var (client, _, connector) = await Start(logs, time, rebooter, policy, back: () => Volatile.Read(ref back));
        await using (client)
        {
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1, Step);
            Volatile.Write(ref back, true);
            await time.AdvanceUntilAsync(() => logs.Events(1105).Count == 1, Step);
            PipeDevice latest = null!;
            for (int i = 0; i < 4; i++) latest = await connector.NextAsync();   // the fourth device is the published one
            Volatile.Write(ref back, false);
            policy.Calls.Clear();
            time.Advance(TimeSpan.FromSeconds(1));
            latest.CloseRemote();
            await time.AdvanceUntilAsync(() => policy.Calls.Count >= 1, Step);
            Assert.Equal((1, 1, 0, 0), (policy.Calls.First().FailedAttempts, policy.Calls.First().FailedAttemptsSinceReboot, policy.Calls.First().SoftReboots, policy.Calls.First().HardReboots));
        }
    }

    [Fact]
    public async Task ReconnectAttempts_CapsAcrossReboots()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter, configure: o => o.ReconnectAttempts = 5);
        await time.AdvanceUntilAsync(() => logs.Events(1106).Count == 1, Step);
        Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
        Assert.Equal(5, connector.Attempts - 1);
        Assert.Equal(("attempts exhausted", "5"), (logs.Events(1106)[0].Value("Reason"), logs.Events(1106)[0].Value("Attempts")));
        Assert.Equal(3, logs.Events(1104).Count);                                   // attempts 1, 2 and 4; 3 ends in 1114, 5 in 1106
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DuringBootWait_ReturnsWithout1106()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var (client, _, connector) = await Start(logs, time, rebooter);
        await time.AdvanceUntilAsync(() => logs.Events(1116).Count == 1, Step);
        int attempts = connector.Attempts;
        await client.DisposeAsync().AsTask().WaitAsync(Limits.Test);                // fake time stands still inside the 2 s wait
        Assert.True(client.Completion.IsCompletedSuccessfully);
        Assert.Equal(attempts, connector.Attempts);
        Assert.Empty(logs.Events(1106));
    }
```

`Start(logs, time, rebooter, policy = null, back = null, restore = null, configure = null)` це статичний помічник класу: `Failing(time, back)`, `Resilient.Seam(logs, time).WithRebooter(rebooter, policy)`, `options.ConnectionRestored = restore`, `configure?.Invoke(options)`, `connector.StartAsync(options)`, потім `time.Advance(1 s)` і `device.CloseRemote()`, повертає `(client, device, connector)`.

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRecoveryTests"`
Expected: FAIL: політика не викликається, `rebooter.Calls` порожній, `AdvanceUntilAsync` кидає `TimeoutException`; `PolicyGivesUp_1106_CompletionFaults` не бачить 1106.

- [ ] **Step 3: Рішення після невдачі і крок перезавантаження**

`DecideAfterFailure` за **Interfaces**. `RebootStepAsync` за спекою 4.3 з такими рішеннями: 1114 лише при `request is null` (`FailedAttempts = state.Attempt`, `Phase = state.Phase`, виняток `lastFailure`); `context = new RebootContext(_target, _link?.Client.RemoteEndPoint, request is not null)` (`_link` це `null` під час першого підключення); `using var timer = new CancellationTokenSource(_options.RebootTimeout, _time); using var linked = CreateLinkedTokenSource(ct, timer.Token)`; виклик `await _rebooter!.RebootAsync(kind, context, linked.Token).WaitAsync(linked.Token)` (так транспорт, що ігнорує токен, не тримає наглядача; виняток покинутої задачі спостерігається через `ContinueWith(t => _ = t.Exception, OnlyOnFaulted)`); `OperationCanceledException when timer.IsCancellationRequested && !ct.IsCancellationRequested` стає `TimeoutException($"The {kind} reboot of {_target} did not complete within {_options.RebootTimeout}.")`; `OperationCanceledException when ct.IsCancellationRequested` летить далі без обліку. Далі лічильник виду і `FailedAttemptsSinceReboot = 0` в обох випадках; невдача: 1115, `request?.Completion.TrySetException(ex)`, `ClearRebootRequest`, повернення. Успіх: `bootTime = _rebooter.GetBootTime(kind)` у `try`; виняток, від'ємне значення або понад `int.MaxValue` мс (`new InvalidOperationException($"GetBootTime({kind}) returned {bootTime}, which is not between zero and Int32.MaxValue milliseconds.")`) ідуть шляхом невдачі; 1116; `state.AfterReboot = kind`; `await Task.Delay(bootTime, _time, ct)`; `ClearRebootRequest`; при `request is not null` `state.ManualWaiters.Add(request)`. Кожен виклик логера у власному `try`.

- [ ] **Step 4: Серії в наглядачі**

`RecoverAsync(state)` у `.Supervisor.cs`:

```csharp
RebootKind? scheduled = null;
Exception? scheduledFailure = null;
while (true)
{
    RebootRequest? request = TakeRebootRequest();
    if (request is not null || scheduled is not null)
    {
        RebootKind kind = request?.Kind == RebootKind.Hard || scheduled == RebootKind.Hard ? RebootKind.Hard : RebootKind.Soft;
        await RebootStepAsync(kind, state, request, scheduledFailure, _lifetime.Token).ConfigureAwait(false);
        (scheduled, scheduledFailure) = (null, null);
    }

    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, CurrentWake());
    ResilienceContext context = ResilienceContextPool.Shared.Get(linked.Token);
    try
    {
        context.Properties.Set(ReconnectKey, state);
        return await _reconnect.ExecuteAsync(ReconnectOnceAsync, context, state).ConfigureAwait(false);
    }
    catch (RebootScheduledException ex) { (scheduled, scheduledFailure) = (ex.Kind, ex.InnerException); }
    catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { /* the wake: the next turn takes the request */ }
    catch (RecoveryGaveUpException) { throw; }
    finally { ResilienceContextPool.Shared.Return(context); }
}
```

`SuperviseAsync` викликає `RecoverAsync(state)` замість `_reconnect.ExecuteAsync`, передає `WatchAsync(link, CurrentWake())`, а після `Publish` завершує `state.ManualWaiters` (`TrySetResult`) перед 1105; у `catch when (_lifetime.IsCancellationRequested)` провалює їх `ClosedException()`, у звичайному `catch` після `GiveUp` провалює їх `_failure`. Поки ручного запиту немає (задача 6), слот завжди порожній. `ReconnectOnceAsync`: перед підлогою `if (state.Attempt >= _options.ReconnectAttempts) ExceptionDispatchInfo.Throw(state.LastFailure!)`; `state.Attempt++` переноситься після підлоги (пробудження під час підлоги не споживає номер спроби); підлога чекає на `ct` контексту, а `OpenLinkAsync` і `RestoreAsync` отримують `_lifetime.Token`; у `catch`: `OperationCanceledException when ct.IsCancellationRequested`, `FatalRestoreException` і будь-який виняток при `_lifetime.IsCancellationRequested` летять як є, решта після `CloseLinkAsync` стає `throw DecideAfterFailure(state, ex, checkSlot: true)`. `ShouldHandle` pipeline перепідключення: виняток не `null`, не `FatalRestoreException`, не `RebootScheduledException`, не `RecoveryGaveUpException`, токен не скасовано і `state.Attempt < _options.ReconnectAttempts` (стан з `a.Context.Properties`). `RestoreAsync` передає `state.AfterReboot` у `ConnectionRestoredContext`. `WatchAsync`/`HeartbeatTurnAsync`: кожен `Task.Delay` і умова циклу беруть `wake` замість `_lifetime.Token` (`wake` уже містить його), `catch when (_lifetime.IsCancellationRequested || wake.IsCancellationRequested)`, а завершення внутрішнього клієнта чекається лише коли не прокинулися. `SuperviseAsync` після `WatchAsync`: `woken = !link.Client.Completion.IsCompleted`; при `woken` причина `new IOException("Reboot requested.")`, записана в `link.LossCause` під `_sync`, без 1103; решта як зараз. `GiveUp` за **Interfaces**.

- [ ] **Step 5: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRecoveryTests|FullyQualifiedName~ResilientReconnectTests|FullyQualifiedName~ResilientHeartbeatTests|FullyQualifiedName~ResilientRestoreTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 6: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.Recovery.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr/Control/ResilientControlClient.cs NetSdr.Tests/Control/ResilientRecoveryTests.cs
git commit -m "feat: escalate to a soft then a hard reboot through the recovery policy while reconnecting" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Ручне `RebootAsync` на живому з'єднанні

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.Recovery.cs`, `NetSdr/Control/ResilientControlClient.Supervisor.cs` (`DisposeAsync`, `GiveUp`)
- Test: `NetSdr.Tests/Control/ResilientRebootTests.cs`

**Interfaces:**
- Consumes: `ThrowIfReentrant`, `ThrowIfClosed`, `_rebooter`, `RebootRequest`, `_rebootRequest`, `_rebootWake`, `TakeRebootRequest`, `RecoverAsync`, `WatchAsync(link, wake)` (5); `FakeRebooter.Booting`, `RecordingPolicy`.
- Produces:
  - `public Task RebootAsync(RebootKind kind, CancellationToken ct = default)` з XML-документацією спеки 3.2 і винятками спеки 4.2: `InvalidOperationException` (з колбеку; без `Rebooter`, текст `"No Rebooter is configured; set ResilientControlClientOptions.Rebooter."`; після відмови), `ObjectDisposedException`, `TimeoutException` і виняток транспорту в задачі.
  - `private Task RegisterRebootRequest(RebootKind kind)` (спека 5.2, злиття 4.2): під `_sync` порожній слот отримує новий `RebootRequest`, запит, що ще не виконується, зливається (`Hard`, якщо хоч один `Hard`), запит, що виконується, приєднує без змін; поза замком скасовується захоплений `_rebootWake` лише в перших двох випадках; повертає `request.Completion.Task`.
  - `private RebootRequest? DropRebootRequest()`: під `_sync` забирає і очищає слот; `DisposeAsync` провалює його `ObjectDisposedException`, `GiveUp` винятком відмови, обидва поза замком.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class ResilientRebootTests
{
    const string Outer = "NetSdr.Control.ResilientControlClient";

    /// <summary>A server, a client with a FakeRebooter that boots the server through CloseOnAccept for 200 ms, Fast options.</summary>
    static async Task<(NetSdrTestServer Server, ResilientControlClient Client, FakeRebooter Rebooter)> StartAsync(
        FakeLoggerFactory logs, Action<ResilientControlClientOptions>? configure = null, Action<NetSdrTestServer>? setup = null)
    {
        var options = Resilient.Fast(logs);
        configure?.Invoke(options);
        FakeRebooter rebooter = null!;
        var (server, client) = await Resilient.StartAsync(options, s =>
        {
            setup?.Invoke(s);
            options.WithRebooter(rebooter = FakeRebooter.Booting(s, TimeSpan.FromMilliseconds(200)), options.RecoveryPolicy);
        });
        return (server, client, rebooter);
    }

    [Fact]
    public async Task RebootAsync_Connected_RestoresWithAfterReboot()
    {
        var logs = new FakeLoggerFactory();
        RebootKind? seen = null;
        var (server, client, rebooter) = await StartAsync(logs, o => o.ConnectionRestored = (ctx, _) => { seen = ctx.AfterReboot; return Task.CompletedTask; });
        await using (server)
        await using (client)
        {
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);
            Assert.True(client.IsConnected);
            Assert.Equal(RebootKind.Soft, seen);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            var context = Assert.Single(rebooter.Contexts);
            Assert.Equal((true, client.RemoteEndPoint, $"127.0.0.1:{server.Port}"), (context.Requested, context.LastRemoteEndPoint, context.Target));
            Assert.Equal((LogLevel.Information, "Soft"), (Assert.Single(logs.Events(1113)).Level, logs.Events(1113)[0].Value("Kind")));
            Assert.Single(logs.Events(1116));
            Assert.Single(logs.Events(1105));
            Assert.Empty(logs.Events(1103));
            Assert.Empty(logs.Events(1114));
        }
    }

    [Fact]
    public async Task RebootAsync_JoinsRunningReboot()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        await using (client)
        {
            var soft = client.RebootAsync(RebootKind.Soft);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);                 // accepted: the 300 ms boot wait runs
            var hard = client.RebootAsync(RebootKind.Hard);                             // joins, whatever its kind
            await Task.WhenAll(soft, hard).WaitAsync(Limits.Test);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            Assert.Equal(2, logs.Events(1113).Count);
            Assert.Single(logs.Events(1105));
        }
    }

    [Fact]
    public async Task RebootAsync_TransportFails_CallerGetsException_ClientReconnects()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        rebooter.OnReboot = (_, _, _) => Task.FromException(new ApplicationException("no service"));
        await using (server)
        await using (client)
        {
            await Assert.ThrowsAsync<ApplicationException>(() => client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test));
            Assert.IsType<ApplicationException>(Assert.Single(logs.Events(1115)).Exception);
            await Eventually.ThatAsync(() => client.IsConnected && logs.Events(1105).Count == 1);
            Assert.Empty(logs.Events(1116));
            Assert.Empty(logs.Events(1103));
        }
    }

    [Fact]
    public async Task RebootAsync_CallerCancels_RebootStillHappens()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rebooter.OnReboot = async (_, _, _) => { entered.TrySetResult(); await gate.Task; };
        await using (server)
        await using (client)
        {
            using var cancel = new CancellationTokenSource();
            var reboot = client.RebootAsync(RebootKind.Soft, cancel.Token);
            await entered.Task.WaitAsync(Limits.Test);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reboot.WaitAsync(Limits.Test));
            gate.SetResult();
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 1);
            Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
            Assert.Single(logs.Events(1116));
        }
    }

    [Fact]
    public async Task RebootAsync_CommandInFlight_RetriedAfterRestore()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs, o => o.ResponseTimeout = TimeSpan.FromSeconds(1),
            s => s.OnRequest(AfGain.Code, Resilient.Once(ControlReply.Silent)));
        await using (server)
        await using (client)
        {
            var set = client.SetAsync(new AfGain(0, 7));
            await Eventually.ThatAsync(() => server.Received.Any(r => r.Code == AfGain.Code));   // in flight, unanswered
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);
            Assert.Equal(7, (await set.WaitAsync(Limits.Test)).Level);
            Assert.Equal(2, server.Received.Count(r => r.Code == AfGain.Code));
            Assert.Single(logs.Events(1100));
        }
    }

    [Fact]
    public async Task RebootAsync_WithoutRebooter_Throws()
    {
        var (server, client) = await Resilient.StartAsync(Resilient.Fast());
        await using (server)
        await using (client)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => client.RebootAsync(RebootKind.Soft));
            Assert.Equal("No Rebooter is configured; set ResilientControlClientOptions.Rebooter.", ex.Message);
        }
    }

    [Fact]
    public async Task RebootAsync_AfterDispose_Throws()
    {
        var (server, client, _) = await StartAsync(new FakeLoggerFactory());
        await using (server)
        {
            await client.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => client.RebootAsync(RebootKind.Soft));
        }

        // After a give-up: the failure as the inner exception, like a command.
        var connector = new PipeConnector { Before = (n, _) => n == 1 ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam().WithRebooter(new FakeRebooter());
        options.ReconnectAttempts = 1;
        var (gaveUp, device) = await connector.StartAsync(options);
        device.CloseRemote();
        await Assert.ThrowsAsync<IOException>(() => gaveUp.Completion.WaitAsync(Limits.Test));
        Assert.IsType<IOException>(Assert.Throws<InvalidOperationException>(() => gaveUp.RebootAsync(RebootKind.Soft)).InnerException);
        await gaveUp.DisposeAsync();
    }

    [Fact]
    public async Task RebootAsync_InsideCallback_GivesUp()
    {
        var logs = new FakeLoggerFactory();
        ResilientControlClient? client = null;
        Exception? thrown = null;
        var started = await StartAsync(logs, o => o.ConnectionRestored = (_, _) =>
        {
            try { _ = client!.RebootAsync(RebootKind.Soft); } catch (InvalidOperationException e) { thrown = e; }
            return Task.CompletedTask;
        });
        client = started.Client;
        await using (started.Server)
        await using (client)
        {
            await started.Server.DisconnectClientAsync();
            var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
            Assert.Same(thrown, failure.InnerException);
            Assert.Empty(started.Rebooter.Calls);
        }

        Assert.Equal("ConnectionRestored called the ResilientControlClient", Assert.Single(logs.Events(1106)).Value("Reason"));
    }

    [Fact]
    public async Task RebootAsync_PreCancelledToken_RegistersNothing()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        await using (client)
        {
            var reboot = client.RebootAsync(RebootKind.Soft, new CancellationToken(canceled: true));
            Assert.True(reboot.IsCanceled);
            await Task.Delay(300);
            Assert.Empty(rebooter.Calls);
            Assert.True(client.IsConnected);
            Assert.Empty(logs.Events(1113));
        }
    }

    // Review Focus 4.
    [Fact]
    public async Task RebootAsync_ParallelCallers_OneReboot()
    {
        var logs = new FakeLoggerFactory();
        var (server, client, rebooter) = await StartAsync(logs);
        await using (server)
        await using (client)
        {
            var tasks = new Task[16];
            Parallel.For(0, tasks.Length, i => tasks[i] = client.RebootAsync(i % 2 == 0 ? RebootKind.Soft : RebootKind.Hard));
            await Task.WhenAll(tasks).WaitAsync(Limits.Test);
            Assert.Single(rebooter.Calls);
            Assert.Single(logs.Events(1116));
            Assert.Equal(16, logs.Events(1113).Count);
            Assert.True(client.IsConnected);
        }
    }

    // Review Focus 3.
    [Fact]
    public async Task PolicyBlocks_NothingElseIsHeld()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter { BootTime = TimeSpan.Zero });
        using var gate = new ManualResetEventSlim();
        var inPolicy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new RecordingPolicy { Decide = _ => { inPolicy.TrySetResult(); gate.Wait(Limits.Test); return RecoveryAction.Continue; } };
        bool back = false;
        var connector = new PipeConnector(time)
        {
            Before = (n, _) => n == 1 || Volatile.Read(ref back) ? Task.CompletedTask : Resilient.Refused(),
            Serve = d => d.NakEverythingAsync(),
        };
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter, policy);
        options.CommandTimeout = Timeout.InfiniteTimeSpan;
        var (client, device) = await connector.StartAsync(options);
        await using (client)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            device.CloseRemote();
            await inPolicy.Task.WaitAsync(Limits.Test);                                // the policy blocks the supervisor now
            Assert.False(client.IsConnected);                                          // no lock is held by it
            var command = client.GetAsync<InterfaceVersion>();
            var reboot = client.RebootAsync(RebootKind.Soft);                          // registers at once
            Assert.False(command.IsCompleted || reboot.IsCompleted);
            Volatile.Write(ref back, true);
            gate.Set();
            await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1 && logs.Events(1105).Count == 1, TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<NetSdrNakException>(() => command.WaitAsync(Limits.Test));   // the pipe NAKs it after the restore
            await reboot.WaitAsync(Limits.Test);
        }
    }

    [Fact]
    public async Task Logging_1113To1117_LevelsAndCategory()
    {
        var logs = new FakeLoggerFactory();
        int decisions = 0;
        var policy = new RecordingPolicy { Decide = _ => Interlocked.Increment(ref decisions) == 1 ? throw new ApplicationException("policy") : RecoveryAction.Reboot(RebootKind.Soft) };
        var (server, client, rebooter) = await StartAsync(logs, o =>
        {
            (o.ResponseTimeout, o.LateReplyTimeout, o.RecoveryPolicy) = (TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), policy);
        });
        var booting = rebooter.OnReboot;
        await using (server)
        await using (client)
        {
            await client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test);                          // 1113, 1116
            rebooter.OnReboot = (_, _, _) => Task.FromException(new ApplicationException("no service"));
            await Assert.ThrowsAsync<ApplicationException>(() => client.RebootAsync(RebootKind.Hard).WaitAsync(Limits.Test));   // 1113, 1115
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 2);
            rebooter.OnReboot = booting;
            server.Availability = ServerAvailability.Silent;                                            // 1102, 1103, then 1117, 1114, 1116
            await Eventually.ThatAsync(() => logs.Events(1105).Count == 3);
        }

        void Expect(int id, int count, LogLevel level)
        {
            Assert.Equal(count, logs.Events(id).Count);
            Assert.All(logs.Events(id), r => Assert.Equal((level, Outer), (r.Level, r.Category)));
        }

        Expect(1113, 2, LogLevel.Information);
        Expect(1114, 1, LogLevel.Warning);
        Expect(1115, 1, LogLevel.Warning);
        Expect(1116, 2, LogLevel.Information);
        Expect(1117, 1, LogLevel.Warning);
        Assert.Equal(("Soft", "2", "Verify"), (logs.Events(1114)[0].Value("Kind"), logs.Events(1114)[0].Value("FailedAttempts"), logs.Events(1114)[0].Value("Phase")));
        Assert.Empty(logs.Events(1106));
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRebootTests"`
Expected: FAIL: не компілюється, `RebootAsync` немає.

- [ ] **Step 3: `RebootAsync` і слот**

`RebootAsync` за спекою 4.2 у порядку: `ThrowIfReentrant()`, `ThrowIfClosed()`, перевірка `_rebooter`, `ct.IsCancellationRequested` дає `Task.FromCanceled(ct)`; далі `Task completion = RegisterRebootRequest(kind)`, 1113 у `try` (`kind`, `_target`; пишеться на кожен прийнятий виклик), `return completion.WaitAsync(ct)` (скасування `ct` після реєстрації скасовує лише задачу викликача). `RegisterRebootRequest` і `DropRebootRequest` за **Interfaces**. `DisposeAsync` (крок 1 спеки стійкості 7.7, під `_sync`) забирає слот і провалює його `ObjectDisposedException` поза замком; `GiveUp` (крок 3) так само винятком відмови. Наглядач уже (задача 5) бере запит на початку кожної серії, прокидається через `WatchAsync`, пише `IOException("Reboot requested.")` без 1103 і завершує `ManualWaiters` після `Publish`.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRebootTests|FullyQualifiedName~ResilientRecoveryTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.Recovery.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr.Tests/Control/ResilientRebootTests.cs
git commit -m "feat: manual RebootAsync through the supervisor, with merging, joining and caller cancellation" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Ручне `RebootAsync` під час перепідключення

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.Recovery.cs`, `NetSdr/Control/ResilientControlClient.Supervisor.cs` (лише якщо тести виявлять відхилення від 5.2-5.4)
- Test: `NetSdr.Tests/Control/ResilientRebootTests.cs`

**Interfaces:**
- Consumes: `PipeConnector.NextAsync`, `PipeDevice.ReadRequestAsync`/`SendAsync`/`CloseRemote`, `RecordingPolicy`, `TakeRebootRequest`, `DecideAfterFailure(checkSlot: true)`, перевірка межі перед спробою (5).
- Produces: нових членів немає; задача пиняє таблицю спеки 4.2 і Review Focus 1.

- [ ] **Step 1: Написати тести, що падають**

Додати до `ResilientRebootTests` (шов, `FakeTimeProvider`, `FakeRebooter { BootTime = TimeSpan.Zero }`, `Serve = null`, тож кожну перевірку відповідає тест; `Step` 100 мс):

```csharp
    /// <summary>A seam on fake time whose attempt 1 is answered by StartAsync; later verifications the test answers itself.</summary>
    static async Task<(ResilientControlClient Client, PipeDevice Device, PipeConnector Connector, FakeRebooter Rebooter, FakeLoggerFactory Logs, FakeTimeProvider Time)>
        SeamAsync(Action<ResilientControlClientOptions>? configure = null, Func<int, bool>? accept = null)
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter { BootTime = TimeSpan.Zero });
        var connector = new PipeConnector(time) { Before = (n, _) => accept is null || accept(n) ? Task.CompletedTask : Resilient.Refused() };
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter);
        configure?.Invoke(options);
        var (client, device) = await connector.StartAsync(options);
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        return (client, device, connector, rebooter, logs, time);
    }

    static async Task<PipeDevice> PendingVerifyAsync(PipeConnector connector)
    {
        PipeDevice device = await connector.NextAsync();
        Assert.Equal(Hex.Parse(Resilient.GetStatus), await device.ReadRequestAsync());
        return device;
    }

    [Fact]
    public async Task RebootAsync_DuringBackoff_InterruptsPause()
    {
        bool back = false;
        var (client, _, connector, rebooter, logs, time) = await SeamAsync(accept: n => n == 1 || Volatile.Read(ref back));
        await using (client)
        {
            DateTimeOffset lost = time.GetUtcNow();
            await time.AdvanceUntilAsync(() => logs.Events(1104).Count == 3, TimeSpan.FromMilliseconds(100));   // 0, 1, 3 s failed; the 4 s pause runs
            time.Advance(TimeSpan.FromSeconds(1));
            Volatile.Write(ref back, true);
            var reboot = client.RebootAsync(RebootKind.Soft);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);               // fake time did not move: the pause was interrupted
            var device = await PendingVerifyAsync(connector);
            await device.SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.InRange((connector.AttemptTimes.Last() - lost).TotalSeconds, 4, 4.3);
            Assert.Equal(5, connector.Attempts);
        }
    }

    [Fact]
    public async Task RebootAsync_DuringAttempt_WaitsForItToEnd()
    {
        var policy = new RecordingPolicy();
        var (client, _, connector, rebooter, logs, _) = await SeamAsync(o => o.RecoveryPolicy = policy);
        await using (client)
        {
            var attempt = await PendingVerifyAsync(connector);
            var reboot = client.RebootAsync(RebootKind.Soft);
            await Task.Delay(200);
            Assert.Empty(rebooter.Calls);                                                // the attempt runs on
            Assert.True(attempt.Client.IsConnected);
            attempt.CloseRemote();                                                       // it fails: the request runs instead of the pause
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);
            Assert.Empty(policy.Calls);                                                  // no policy call for that failure
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
        }
    }

    [Fact]
    public async Task RebootAsync_AttemptSucceededMeanwhile_StillReboots()
    {
        var (client, _, connector, rebooter, logs, _) = await SeamAsync();
        await using (client)
        {
            var attempt = await PendingVerifyAsync(connector);
            var reboot = client.RebootAsync(RebootKind.Soft);
            await attempt.SendAsync(Resilient.Nak);                                      // published, then the request runs at once
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await reboot.WaitAsync(Limits.Test);
            Assert.Equal(2, logs.Events(1105).Count);
            Assert.Single(logs.Events(1103));                                            // the requested loss is not reported
        }
    }

    [Fact]
    public async Task RebootAsync_Coalesce_HardWins()
    {
        var (client, _, connector, rebooter, logs, _) = await SeamAsync();
        await using (client)
        {
            var attempt = await PendingVerifyAsync(connector);
            var soft = client.RebootAsync(RebootKind.Soft);
            var hard = client.RebootAsync(RebootKind.Hard);
            await attempt.SendAsync(Resilient.Nak);
            await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);
            await (await PendingVerifyAsync(connector)).SendAsync(Resilient.Nak);
            await Task.WhenAll(soft, hard).WaitAsync(Limits.Test);
            Assert.Equal(new[] { RebootKind.Hard }, rebooter.Calls);
            Assert.Equal(2, logs.Events(1113).Count);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task RebootAsync_ManualCountsTowardsEscalation(int maxReboots, int expectedReboots)
    {
        var policy = new RecordingPolicy { Decide = new EscalatingRecoveryPolicy { MaxRebootsPerLoss = maxReboots }.OnAttemptFailed };
        var (client, _, connector, rebooter, logs, time) = await SeamAsync(o => o.RecoveryPolicy = policy, accept: n => n == 1);
        var reboot = client.RebootAsync(RebootKind.Hard);                                // noticed loss or not, the request runs before the first series
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == expectedReboots && policy.Calls.Count >= 6, TimeSpan.FromMilliseconds(100));
        Assert.Equal(Enumerable.Repeat(RebootKind.Hard, expectedReboots), rebooter.Calls);
        Assert.All(policy.Calls, c => Assert.Equal(0, c.SoftReboots));
        Assert.Equal(1, policy.Calls.First().HardReboots);
        if (expectedReboots == 2) Assert.Equal(3, policy.Calls.Count(c => c.HardReboots == 1));   // three failures after the manual hard, then the second
        await client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reboot.WaitAsync(Limits.Test));
    }

    // Review Focus 1.
    [Fact]
    public async Task RebootAsync_OnLastAllowedAttempt_RebootsThenGiveUpFailsTheCaller()
    {
        var (client, _, connector, rebooter, logs, _) = await SeamAsync(o => o.ReconnectAttempts = 2);
        var attempt = await PendingVerifyAsync(connector);                               // attempt 2 of 2
        var reboot = client.RebootAsync(RebootKind.Soft);
        attempt.CloseRemote();
        await Eventually.ThatAsync(() => rebooter.Calls.Count == 1);                     // the reboot still runs
        var failure = await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => reboot.WaitAsync(Limits.Test)));
        Assert.Equal(("attempts exhausted", "2"), (Assert.Single(logs.Events(1106)).Value("Reason"), logs.Events(1106)[0].Value("Attempts")));
        Assert.Equal(3, connector.Attempts);                                             // no attempt beyond the cap
        await client.DisposeAsync();
    }
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRebootTests"`
Expected: PASS для більшості: механіку задали задачі 5 і 6. Падіння тут означає відхилення від спеки 5.2-5.4, яке треба виправити, не послаблюючи тест.

- [ ] **Step 3: Звірити інваріанти слоту**

Перевірити в коді (і виправити, якщо тести впали) чотири правила: `TakeRebootRequest` підміняє `_rebootWake` навіть при порожньому слоті (інакше хибне пробудження зациклює серію); приєднання до запиту, що виконується, не скасовує `_rebootWake` (інакше серія після завантаження прокинеться без запиту); `DecideAfterFailure` дивиться в слот до політики; перевірка `state.Attempt >= ReconnectAttempts` стоїть до підлоги й до `state.Attempt++`.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientRebootTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.Recovery.cs NetSdr/Control/ResilientControlClient.Supervisor.cs NetSdr.Tests/Control/ResilientRebootTests.cs
git commit -m "test: manual reboots during backoff, attempts and the last allowed attempt" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Перше підключення через драбину

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.cs` (`ConnectCoreAsync`, конструктор, `_firstConnect`), `NetSdr/Control/ResilientControlClient.Recovery.cs` (`ConnectOnceAsync`)
- Test: `NetSdr.Tests/Control/ResilientFirstConnectTests.cs`

**Interfaces:**
- Consumes: `_connectAttempts` (2), `DecideAfterFailure`, `RebootStepAsync`, `ReconnectState`, `ReconnectKey`, `OpenLinkAsync`, `AttemptFloor`, `_lastAttemptStart`, `Publish`, `SuperviseAsync`.
- Produces:
  - Поле `private readonly ResiliencePipeline _firstConnect;`: при `_connectAttempts == 1` `ResiliencePipeline.Empty`, інакше та сама стратегія, що `_reconnect` (`MaxRetryAttempts = _connectAttempts - 1`, експоненційно 1 с до 30 с, `UseJitter` з опцій), `ShouldHandle` як у `_reconnect`, але межа `state.Attempt < _connectAttempts`, `OnRetry` пише 1118 (`state.Attempt`, `_connectAttempts`, `_target`, `state.Phase`, `a.RetryDelay`, виняток).
  - `private async ValueTask<Link> ConnectOnceAsync(ResilienceContext context, ReconnectState state)`: спроба першого підключення (спека 4.5): перевірка межі (`ExceptionDispatchInfo.Throw(state.LastFailure!)`), підлога 1 с на `context.CancellationToken`, `state.Attempt++`, `_lastAttemptStart`, `OpenLinkAsync(phase => state.Phase = phase, context.CancellationToken)`, без фази `Restore`; `OperationCanceledException` при скасованому токені летить як є, інша невдача стає `throw DecideAfterFailure(state, ex, checkSlot: false)`.
  - `ConnectCoreAsync(ct)`: `state = new ReconnectState(null, _time.GetUtcNow(), _time.GetTimestamp())`, цикл серій як `RecoverAsync`, але без слоту: `RebootScheduledException` веде до `RebootStepAsync(ex.Kind, state, null, ex.InnerException, ct)`, `RecoveryGaveUpException` до `ExceptionDispatchInfo.Throw(ex.InnerException!)`; будь-який інший виняток летить як є (`SocketException`, `TimeoutException`, `IOException`, `OperationCanceledException`). Після серії `Publish`, наглядач і 1109 як зараз; `state` відкидається.

- [ ] **Step 1: Написати тести, що падають**

Додати до `ResilientFirstConnectTests` (`Step` 100 мс; `Connect(connector, options, ct = default)` це `ResilientControlClient.ConnectAsync(connector.ConnectAsync, "pipe", options, ct)`):

```csharp
    [Fact]
    public async Task NoRebooter_DefaultIsOneAttempt()
    {
        var logs = new FakeLoggerFactory();
        var refused = new PipeConnector { Before = (_, _) => Resilient.Refused() };
        await Assert.ThrowsAsync<SocketException>(() => Connect(refused, Resilient.Seam(logs)).WaitAsync(Limits.Test));
        Assert.Equal(1, refused.Attempts);
        Assert.Empty(logs.Events(1118));
    }

    [Fact]
    public async Task ExplicitConnectAttempts_RetriesWithBackoff()
    {
        var (logs, time) = (new FakeLoggerFactory(), new FakeTimeProvider());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var options = Resilient.Seam(logs, time);
        options.ConnectAttempts = 3;
        DateTimeOffset start = time.GetUtcNow();
        var connecting = Connect(refused, options);
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThrowsAsync<SocketException>(() => connecting);
        double[] starts = refused.AttemptTimes.Select(t => (t - start).TotalSeconds).ToArray();
        double[] expected = [0, 1, 3];
        Assert.Equal(3, starts.Length);
        for (int i = 0; i < 3; i++) Assert.InRange(starts[i], expected[i], expected[i] + 0.3);
        Assert.Equal(new[] { ("1", "3", "Connect", 1.0), ("2", "3", "Connect", 2.0) },
            logs.Events(1118).Select(r => (r.Value("Attempt"), r.Value("Attempts"), r.Value("Phase"), r.Span("Delay").TotalSeconds)));
        Assert.All(logs.Events(1118), r => Assert.Equal((LogLevel.Warning, "pipe"), (r.Level, r.Value("Target"))));
        Assert.Empty(logs.Events(1104));
    }

    [Fact]
    public async Task Rebooter_DefaultEightAttempts_FullLadder()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThrowsAsync<SocketException>(() => connecting);
        Assert.Equal(8, refused.Attempts);
        Assert.Equal(new[] { RebootKind.Soft, RebootKind.Hard }, rebooter.Calls);
        Assert.Equal(5, logs.Events(1118).Count);                                        // attempts 1, 2, 4, 5, 7; 3 and 6 end in 1114, 8 in the throw
        Assert.Equal(2, logs.Events(1114).Count);
        Assert.Equal(2, logs.Events(1116).Count);
        Assert.Empty(logs.Events(1109));
    }

    [Fact]
    public async Task Rebooter_RecoversAfterSoft()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        int callbacks = 0;
        var connector = new PipeConnector(time)
        {
            Before = (_, _) => rebooter.Calls.Count > 0 ? Task.CompletedTask : Resilient.Refused(),
            Serve = d => d.NakEverythingAsync(),
        };
        var options = Resilient.Seam(logs, time).WithRebooter(rebooter);
        options.ConnectionRestored = (_, _) => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; };
        var connecting = Connect(connector, options);
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await using var client = await connecting;
        Assert.True(client.IsConnected);
        Assert.Equal((4, 0), (connector.Attempts, callbacks));
        Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);
        Assert.Single(logs.Events(1109));
        Assert.Empty(logs.Events(1105));
    }

    [Fact]
    public async Task FirstConnect_RebootContext()
    {
        var (time, rebooter) = (new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(time: time).WithRebooter(rebooter, new EscalatingRecoveryPolicy { SoftRebootAfter = 1, MaxRebootsPerLoss = 1 }));
        await time.AdvanceUntilAsync(() => rebooter.Calls.Count == 1, Step);
        var context = Assert.Single(rebooter.Contexts);
        Assert.Equal(("pipe", null, false), (context.Target, context.LastRemoteEndPoint, context.Requested));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThrowsAsync<SocketException>(() => connecting);
    }

    [Fact]
    public async Task FirstConnect_CancelDuringBootWait_Throws_NothingRuns()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        using var cancel = new CancellationTokenSource();
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter, new EscalatingRecoveryPolicy { SoftRebootAfter = 1 }), cancel.Token);
        await time.AdvanceUntilAsync(() => logs.Events(1116).Count == 1, Step);         // inside the 2 s boot wait
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(Limits.Test));
        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.Equal(1, refused.Attempts);                                               // nothing went on after the cancellation
        Assert.Empty(logs.Events(1109));
    }

    [Fact]
    public async Task FirstConnect_PolicyGivesUp_ThrowsLastFailure()
    {
        var (logs, time, rebooter) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter());
        var refused = new PipeConnector(time) { Before = (_, _) => Resilient.Refused() };
        var connecting = Connect(refused, Resilient.Seam(logs, time).WithRebooter(rebooter, new RecordingPolicy { Decide = _ => RecoveryAction.GiveUp }));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await Assert.ThrowsAsync<SocketException>(() => connecting);
        Assert.Equal(1, refused.Attempts);
        Assert.Empty(logs.Events(1106));
        Assert.Empty(rebooter.Calls);
    }

    [Fact]
    public async Task FirstLoss_CountersStartAtZero()
    {
        var (logs, time, rebooter, policy) = (new FakeLoggerFactory(), new FakeTimeProvider(), new FakeRebooter(), new RecordingPolicy());
        bool refuse = true;
        var connector = new PipeConnector(time)
        {
            Before = (_, _) => Volatile.Read(ref refuse) && rebooter.Calls.Count == 0 ? Resilient.Refused() : Task.CompletedTask,
            Serve = d => d.NakEverythingAsync(),
        };
        policy.Decide = new EscalatingRecoveryPolicy().OnAttemptFailed;
        var connecting = Connect(connector, Resilient.Seam(logs, time).WithRebooter(rebooter, policy));
        await time.AdvanceUntilAsync(() => connecting.IsCompleted, Step);
        await using var client = await connecting;
        Assert.Equal(new[] { RebootKind.Soft }, rebooter.Calls);                                  // one soft during the first connect
        policy.Decide = _ => RecoveryAction.Continue;
        policy.Calls.Clear();
        PipeDevice device = await connector.NextAsync();                                 // the only device: refused attempts attach none
        rebooter.Calls.Clear();                                                          // Before refuses again
        time.Advance(TimeSpan.FromSeconds(1));
        device.CloseRemote();
        await time.AdvanceUntilAsync(() => policy.Calls.Count >= 1, Step);
        Assert.Equal((1, 1, 0, 0), (policy.Calls.First().FailedAttempts, policy.Calls.First().FailedAttemptsSinceReboot, policy.Calls.First().SoftReboots, policy.Calls.First().HardReboots));
    }
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientFirstConnectTests"`
Expected: FAIL: `ExplicitConnectAttempts_RetriesWithBackoff` бачить одну спробу і жодного 1118; `Rebooter_RecoversAfterSoft` кидає `SocketException`.

- [ ] **Step 3: Реалізація**

За **Interfaces**. `ConnectCoreAsync` більше не ставить `_lastAttemptStart` перед першою спробою: це робить `ConnectOnceAsync` після підлоги, тож перша спроба після ранньої втрати так само чекає залишок 1 с. Скасований `ct` викликача перериває підлогу (токен контексту), спробу (`OpenLinkAsync`), транспорт і очікування завантаження (`RebootStepAsync(..., ct)`); після винятку нічого не працює, бо `OpenLinkAsync` закриває свій Link сам. XML-документація `ConnectAsync` оновлюється в задачі 11.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~ResilientFirstConnectTests|FullyQualifiedName~ResilientConnectTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.cs NetSdr/Control/ResilientControlClient.Recovery.cs NetSdr.Tests/Control/ResilientFirstConnectTests.cs
git commit -m "feat: run the first ConnectAsync through ConnectAttempts and the recovery ladder" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Vega: сервісний протокол і `VegaRebooter`

**Files:**
- Modify: `examples/Vega/NetSdr.Examples.Vega/VegaProtocol.cs`
- Create: `examples/Vega/NetSdr.Examples.Vega/VegaRebooter.cs`, `examples/Vega/NetSdr.Examples.Vega.Tests/VegaServiceServer.cs`
- Test: `examples/Vega/NetSdr.Examples.Vega.Tests/Items/VegaRebooterTests.cs`

**Interfaces:**
- Consumes: `IDeviceRebooter`, `RebootContext` (публічний конструктор), `RebootKind`, `VegaException(string message, DeviceIdentity? identity = null)`, `Hex.Parse` тестів Vega.
- Produces:
  - `VegaProtocol`: `public const int ServicePort = 50001; public const byte ServiceVersion = 1, ServiceMagic0 = 0x56, ServiceMagic1 = 0x53, SoftRebootCommand = 1, HardRebootCommand = 2, ServiceAccepted = 0, ServiceBadKey = 1, ServiceBusy = 2;` з документацією формату спеки 6.1 (запит 8 байт, відповідь 4, пристрій закриває з'єднання після відповіді).
  - `public sealed class VegaRebooterOptions { public string? Host { get; init; } public int Port { get; init; } = VegaProtocol.ServicePort; public uint UnlockKey { get; init; } public TimeSpan SoftBootTime { get; init; } = TimeSpan.FromSeconds(8); public TimeSpan HardBootTime { get; init; } = TimeSpan.FromSeconds(20); }`.
  - `public sealed class VegaRebooter : IDeviceRebooter { public VegaRebooter(VegaRebooterOptions options); public Task RebootAsync(RebootKind kind, RebootContext context, CancellationToken ct); public TimeSpan GetBootTime(RebootKind kind); }`; конструктор кидає `ArgumentNullException` і `ArgumentOutOfRangeException` (`Port` поза 1..65535, від'ємний час завантаження).
  - Тести: `public sealed class VegaServiceServer(Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> respond) : IAsyncDisposable { public int Port { get; } public IReadOnlyList<byte[]> Requests { get; } public Task StartAsync(); public ValueTask DisposeAsync(); }`: `TcpListener` на loopback з портом 0; на кожне з'єднання читає рівно 8 байт (`ReadExactlyAsync`), записує їх, пише `respond(request)` і закриває сокет; клієнт, що пішов раніше, ігнорується.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class VegaRebooterTests
{
    const uint Key = 0xC0DE5EC5;

    static RebootContext Ctx(IPEndPoint? last = null, string target = "127.0.0.1:50000") => new(target, last, requested: false);

    static async Task<VegaServiceServer> ServeAsync(string replyHex)
    {
        var server = new VegaServiceServer(_ => Hex.Parse(replyHex));
        await server.StartAsync();
        return server;
    }

    static VegaRebooter Rebooter(int port, string? host = "127.0.0.1", uint key = Key) =>
        new(new VegaRebooterOptions { Host = host, Port = port, UnlockKey = key });

    /// <summary>One soft reboot against a service that answers <paramref name="reply"/>; the exception it ended with, or null.</summary>
    static async Task<Exception?> RebootAgainstAsync(string reply, RebootKind kind = RebootKind.Soft)
    {
        await using var server = await ServeAsync(reply);
        return await Record.ExceptionAsync(() => Rebooter(server.Port).RebootAsync(kind, Ctx(), default).WaitAsync(Limits.Test));
    }

    static async Task AssertRequestAsync(RebootKind kind, string reply, string expectedRequest)
    {
        await using var server = await ServeAsync(reply);
        await Rebooter(server.Port).RebootAsync(kind, Ctx(), default).WaitAsync(Limits.Test);
        Assert.Equal(Hex.Parse(expectedRequest), Assert.Single(server.Requests));
    }

    [Fact] public Task Soft_SendsExactBytes() => AssertRequestAsync(RebootKind.Soft, "56 53 01 00", "56 53 01 01 C5 5E DE C0");
    [Fact] public Task Hard_SendsExactBytes() => AssertRequestAsync(RebootKind.Hard, "56 53 02 00", "56 53 01 02 C5 5E DE C0");

    [Fact] public async Task BadKey_VegaException() => Assert.Contains("wrong unlock key", Assert.IsType<VegaException>(await RebootAgainstAsync("56 53 01 01")).Message);
    [Fact] public async Task Busy_VegaException() => Assert.Contains("busy", Assert.IsType<VegaException>(await RebootAgainstAsync("56 53 01 02")).Message);
    [Fact] public async Task OtherStatus_VegaException() => Assert.Contains("status 7", Assert.IsType<VegaException>(await RebootAgainstAsync("56 53 01 07")).Message);
    [Fact] public async Task TruncatedReply_IOException() => Assert.IsAssignableFrom<IOException>(await RebootAgainstAsync("56 53"));
    [Fact] public async Task WrongMagic_IOException() => Assert.IsType<IOException>(await RebootAgainstAsync("00 00 01 00"));
    [Fact] public async Task WrongCommand_IOException() => Assert.IsType<IOException>(await RebootAgainstAsync("56 53 02 00"));   // the command byte must echo the request

    [Fact]
    public async Task HostFromContext_UsesLastRemoteEndPoint()
    {
        await using var server = await ServeAsync("56 53 01 00");
        var context = Ctx(new IPEndPoint(IPAddress.Loopback, 50000), target: "nowhere.invalid:50000");
        await Rebooter(server.Port, host: null).RebootAsync(RebootKind.Soft, context, default).WaitAsync(Limits.Test);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task HostFromTarget_WhenNoEndPoint()
    {
        await using var server = await ServeAsync("56 53 01 00");
        await Rebooter(server.Port, host: null).RebootAsync(RebootKind.Soft, Ctx(target: "127.0.0.1:50000"), default).WaitAsync(Limits.Test);
        Assert.Single(server.Requests);
    }

    [Fact]
    public void GetBootTime_FromOptions()
    {
        var defaults = new VegaRebooter(new VegaRebooterOptions { UnlockKey = Key });
        Assert.Equal((8, 20), ((int)defaults.GetBootTime(RebootKind.Soft).TotalSeconds, (int)defaults.GetBootTime(RebootKind.Hard).TotalSeconds));
        var custom = new VegaRebooter(new VegaRebooterOptions { UnlockKey = Key, SoftBootTime = TimeSpan.FromSeconds(1), HardBootTime = TimeSpan.FromSeconds(2) });
        Assert.Equal((1, 2), ((int)custom.GetBootTime(RebootKind.Soft).TotalSeconds, (int)custom.GetBootTime(RebootKind.Hard).TotalSeconds));
        Assert.Equal((VegaProtocol.ServicePort, 50001), (new VegaRebooterOptions().Port, VegaProtocol.ServicePort));
    }

    [Theory]
    [InlineData(0, 8, 20)]
    [InlineData(65536, 8, 20)]
    [InlineData(50001, -1, 20)]
    [InlineData(50001, 8, -1)]
    public void InvalidOptions_Throw(int port, int softSeconds, int hardSeconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VegaRebooter(new VegaRebooterOptions
        {
            Port = port, SoftBootTime = TimeSpan.FromSeconds(softSeconds), HardBootTime = TimeSpan.FromSeconds(hardSeconds),
        }));
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~VegaRebooterTests"`
Expected: FAIL: не компілюється, `VegaRebooter`, `VegaServiceServer` і констант сервісу немає.

- [ ] **Step 3: Реалізація**

`VegaRebooter.RebootAsync` за спекою 6.2: адреса `options.Host ?? context.LastRemoteEndPoint?.Address.ToString() ?? HostOf(context.Target)`, де `HostOf` повертає `IPEndPoint.TryParse(target, out var ep) ? ep.Address.ToString() : target[..target.LastIndexOf(':')]` (без двокрапки весь рядок); одне з'єднання на запит: `Socket(SocketType.Stream, ProtocolType.Tcp)`, `ConnectAsync(host, port, ct)`, запис 8 байт (`ServiceMagic0`, `ServiceMagic1`, `ServiceVersion`, команда, ключ little-endian через `BinaryPrimitives`), `ReadExactlyAsync` 4 байт (`EndOfStreamException` стає `IOException("The Vega service closed the connection before answering.")`), закриття. Неправильний magic або команда у відповіді дає `IOException("The Vega service answered with an unexpected reply.")`; статуси за спекою: 1 `VegaException("The Vega service rejected the reboot: wrong unlock key.")`, 2 `VegaException("The Vega service is busy and did not accept the reboot.")`, інший `VegaException($"The Vega service answered with status {status}.")`. `GetBootTime` повертає `SoftBootTime` або `HardBootTime`. `VegaServiceServer` за **Interfaces**; `respond` викликається на потоці з'єднання, поза замками сервера.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~VegaRebooterTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add examples/Vega/NetSdr.Examples.Vega/VegaProtocol.cs examples/Vega/NetSdr.Examples.Vega/VegaRebooter.cs examples/Vega/NetSdr.Examples.Vega.Tests/VegaServiceServer.cs examples/Vega/NetSdr.Examples.Vega.Tests/Items/VegaRebooterTests.cs
git commit -m "feat(vega): service protocol constants and VegaRebooter over TCP 50001" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Vega: емулятор із сервісним сервером, зависанням і перезавантаженням

**Files:**
- Modify: `examples/Vega/NetSdr.Examples.Vega.Tests/VegaEmulator.cs`
- Test: `examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaRecoveryTests.cs`

**Interfaces:**
- Consumes: `VegaServiceServer` (9), `NetSdrTestServer.Availability`, `ClearState`, `StopStreamingAsync`, `DisconnectClientAsync`, `Preload` (3); `VegaRebooter`, `EscalatingRecoveryPolicy`, `ResilientControlClient.RebootAsync`, `VendorUnlock`, `DeviceLabel`, `ProductId`.
- Produces (спека 6.3):
  - `public enum VegaHang { None, Firmware, Board }`.
  - `VegaEmulator`: `public int ServicePort { get; }`, `public TimeSpan SoftBootTime { get; set; } = TimeSpan.FromMilliseconds(200);`, `public TimeSpan HardBootTime { get; set; } = TimeSpan.FromMilliseconds(400);`, `public void Hang(VegaHang kind)`, `public IReadOnlyList<(RebootKind Kind, bool KeyValid)> RebootRequests { get; }`; `StartAsync` запускає обидва сервери, `DisposeAsync` закриває обидва.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class VegaRecoveryTests
{
    // No FakeLogger here: the Vega test project has no logging test package, so the log events of these paths are pinned
    // by ResilientRecoveryTests and ResilientRebootTests; these tests check the emulator and the client state.
    static ResilientControlClientOptions Recovering(VegaEmulator emulator, uint serviceKey = VegaEmulator.DefaultKey) => new()
    {
        ResponseTimeout = TimeSpan.FromMilliseconds(100),
        LateReplyTimeout = TimeSpan.FromMilliseconds(200),
        HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        ConnectTimeout = TimeSpan.FromSeconds(1),
        RebootTimeout = TimeSpan.FromSeconds(2),
        Rebooter = new VegaRebooter(new VegaRebooterOptions
        {
            Port = emulator.ServicePort, UnlockKey = serviceKey,
            SoftBootTime = TimeSpan.FromMilliseconds(300), HardBootTime = TimeSpan.FromMilliseconds(500),   // longer than the emulator's 200 / 400 ms
        }),
        RecoveryPolicy = new EscalatingRecoveryPolicy { SoftRebootAfter = 1, HardRebootAfter = 1 },
        ConnectionRestored = (ctx, ct) => ctx.Client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey), ct),
    };

    static async Task<ResilientControlClient> ConnectAsync(VegaEmulator emulator, ResilientControlClientOptions options)
    {
        var client = await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), options).WaitAsync(Limits.Test);
        await client.SetAsync(new VendorUnlock(VegaEmulator.DefaultKey)).WaitAsync(Limits.Test);
        return client;
    }

    [Fact]
    public async Task FirmwareHang_SoftRebootRecovers_UnlockRestored()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var client = await ConnectAsync(emulator, Recovering(emulator));
        emulator.Hang(VegaHang.Firmware);
        await Eventually.ThatAsync(() => emulator.RebootRequests.Count == 1);           // the hang was found and escalated
        await Eventually.ThatAsync(() => client.IsConnected);
        Assert.Equal(new[] { (RebootKind.Soft, true) }, emulator.RebootRequests);
        Assert.Equal("", (await client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test)).Value);   // served only when unlocked: the callback ran
    }

    [Fact]
    public async Task BoardHang_SoftThenHard()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var client = await ConnectAsync(emulator, Recovering(emulator));
        emulator.Hang(VegaHang.Board);
        await Eventually.ThatAsync(() => emulator.RebootRequests.Count == 2);
        await Eventually.ThatAsync(() => client.IsConnected);
        Assert.Equal(new[] { (RebootKind.Soft, true), (RebootKind.Hard, true) }, emulator.RebootRequests);
        Assert.Equal("", (await client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task ManualHardReboot_ClearsDeviceState()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var client = await ConnectAsync(emulator, Recovering(emulator));
        await client.SetAsync(new DeviceLabel("night shift")).WaitAsync(Limits.Test);
        await client.RebootAsync(RebootKind.Hard).WaitAsync(Limits.Test);
        Assert.Equal(new[] { (RebootKind.Hard, true) }, emulator.RebootRequests);
        Assert.Equal("", (await client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test)).Value);
        Assert.Equal(VegaProtocol.ProductId, (await client.GetAsync<ProductId>().WaitAsync(Limits.Test)).Value);   // preloaded again
    }

    [Fact]
    public async Task WrongServiceKey_RebootFails_1115()
    {
        // 1115 itself is pinned by ResilientRebootTests; here the failure reaches the caller as the VegaException of the service.
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var client = await ConnectAsync(emulator, Recovering(emulator, serviceKey: 0x0BAD_0BAD));
        var ex = await Assert.ThrowsAsync<VegaException>(() => client.RebootAsync(RebootKind.Soft).WaitAsync(Limits.Test));
        Assert.Contains("wrong unlock key", ex.Message);
        Assert.Equal(new[] { (RebootKind.Soft, false) }, emulator.RebootRequests);
        await Eventually.ThatAsync(() => client.IsConnected);                            // the requested loss is reconnected as any other
        Assert.True(emulator.IsUnlocked);                                                 // nothing happened on the device
    }

    [Fact]
    public async Task HungAtStartup_FirstConnectRecoversBySoftReboot()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        emulator.Hang(VegaHang.Firmware);
        await using var client = await ResilientControlClient.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), Recovering(emulator)).WaitAsync(Limits.Test);
        Assert.True(client.IsConnected);
        Assert.Equal(new[] { (RebootKind.Soft, true) }, emulator.RebootRequests);
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<DeviceLabel>().WaitAsync(Limits.Test));   // locked: ConnectionRestored did not run
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~VegaRecoveryTests"`
Expected: FAIL: не компілюється, `ServicePort`, `Hang`, `RebootRequests` немає.

- [ ] **Step 3: Емулятор**

Конструктор створює `Service = new VegaServiceServer(Respond)`; `StartAsync` це `Task.WhenAll(Server.StartAsync(), Service.StartAsync())`, `ServicePort => Service.Port`, `DisposeAsync` закриває обидва. Під `_sync` живуть `VegaHang _hang`, `bool _booting`, `List<(RebootKind, bool)> _rebootRequests`. `Respond(request)`: запит не з `56 53 01` і командою 1 або 2 дає порожню відповідь і не записується; інакше `kind` з команди, `keyValid = BinaryPrimitives.ReadUInt32LittleEndian(request[4..]) == _unlockKey`, запис у `_rebootRequests`; під `_sync`: `_booting` дає статус 2, `!keyValid` статус 1, інакше `_booting = true` і `_ = Task.Run(() => BootAsync(kind))`, статус 0; відповідь `56 53 cmd status`. `BootAsync(kind)` за таблицею спеки 6.3: `await Server.DisconnectClientAsync()`, `Server.Availability = CloseOnAccept`, під `_sync` `_unlocked = false` і зняття `Firmware` (soft) або будь-якого зависання (hard); для hard ще `Server.ClearState()`, `Server.Preload(new ProductId(VegaProtocol.ProductId))`, `_antennas.Clear()`, `_label = string.Empty`; `await Task.Delay(kind == Hard ? HardBootTime : SoftBootTime)`; потім `Server.Availability = _hang == VegaHang.Board ? Silent : Normal` і `_booting = false`. `Hang(kind)`: під `_sync` `_hang = kind`; поза замком `Server.Availability = kind == None ? Normal : Silent` (під час завантаження не чіпається, кінець завантаження виставить сам) і для `kind != None` `_ = Server.StopStreamingAsync()`.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --nologo -v q --filter "FullyQualifiedName~VegaRecoveryTests|FullyQualifiedName~VegaResilienceTests|FullyQualifiedName~VegaConnectTests"`
Expected: PASS. Потім `dotnet test NetSdr.sln --nologo -v q`: усе зелене.

- [ ] **Step 5: Commit**

```bash
git add examples/Vega/NetSdr.Examples.Vega.Tests/VegaEmulator.cs examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaRecoveryTests.cs
git commit -m "test(vega): emulator service server, firmware and board hangs, soft and hard reboots" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Документація

**Files:**
- Modify: `NetSdr/Control/ResilientControlClient.cs` (XML-документація обох `ConnectAsync`), `NetSdr/Control/ResilientControlClientOptions.cs` (`CommandTimeout`), `docs/architecture.md`, `docs/superpowers/specs/2026-10-07-netsdr-device-reboot-design.md`
- Test: немає; перевірка це `dotnet build NetSdr.sln --nologo -v q` без попереджень XML і рендер Mermaid.

**Interfaces:** нових членів немає.

- [ ] **Step 1: XML-документація**

Обидва публічні `ConnectAsync`: речення "There is one attempt and `ConnectionRestored` is not called; on failure nothing is left running." замінюється на опис спеки 4.5: `ConnectAttempts` спроб (1 без `Rebooter`, 8 з ним), паузи 1, 2, 4 ... 30 с, кожна невдала спроба пише Warning 1118, з `Rebooter` після кожної невдачі питається `RecoveryPolicy` і перезавантаження виконується як при перепідключенні, `ConnectionRestored` не викликається навіть після перезавантаження, виняток останньої спроби летить як є, скасування перериває все й нічого не лишає працювати. `<exception>` для `SocketException`, `TimeoutException`, `IOException` отримують "of the last attempt". `CommandTimeout` отримує `<remarks>`: команди під час перезавантаження чекають так само, як під час перепідключення; якщо час завантаження більший за `CommandTimeout`, вони впадуть з `TimeoutException`, тож `CommandTimeout` варто ставити більшим за найдовший час завантаження (спека 3.2).

- [ ] **Step 2: `docs/architecture.md`**

- Розділ 3, вузол `resil`: до опису додати "драбина перезавантажень: IRecoveryPolicy вирішує, IDeviceRebooter виконує"; ребро `app -->|"..."| resil` отримує `RebootAsync(kind)`; нове ребро `resil -->|"OnAttemptFailed, RebootAsync(kind), GetBootTime<br/>[IRecoveryPolicy, IDeviceRebooter застосунку]"| app`; `resil` пише `EventId 1100-1118` не треба міняти (діапазон 1100-1199 уже на ребрі).
- Таблиця 3.1: рядок "Крок перезавантаження | Наглядач | `IDeviceRebooter.RebootAsync` і `GetBootTime` виконуються в наглядачі поза замком, обмежені `RebootTimeout` і `DisposeAsync`; команди чекають у межах `CommandTimeout`, heartbeat не йде".
- Розділ 5 (Vega): вузол `rebooter["<b>VegaRebooter</b><br/>[IDeviceRebooter]<br/>TCP 50001, 8 байт запиту,<br/>4 байти відповіді"]` у `vega`, вузол `idev["<b>IDeviceRebooter</b>"]` у `core`, ребро `rebooter -.->|"реалізує"| idev`; клас `component` для `rebooter`, `framework` для `idev`.
- Розділ 7 (динамічна діаграма): після `loop` додати

```
    opt політика вирішила Reboot(kind) після невдалої спроби
        Sup->>App: IDeviceRebooter.RebootAsync(kind)
        App->>Rx: сервісний канал, наприклад TCP 50001
        Note over Sup: Warning 1114, потім Information 1116 і очікування GetBootTime(kind)
        Sup->>CC: нова серія спроб, паузи знову з 1 с
    end
```

  і пункт списку під діаграмою: ручне `RebootAsync(kind)` іде тим самим шляхом, але без Warning 1103 і 1114, а колбек бачить `context.AfterReboot`. У повідомленнях `sequenceDiagram` немає крапок з комою; `#lt;`/`#gt;` для кутових дужок, як у наявних вузлах.
- Список специфікацій на початку файлу отримує рядок про спеку перезавантаження.

- [ ] **Step 3: Спека**

Рядок 4: `Статус: реалізовано за планом docs/superpowers/plans/2026-10-07-netsdr-device-reboot.md`. У 2.1 рядок `ResilientControlClient.Log.cs` каже "події 1113-1118" (1118 додано в 4.5 і 8 пізніше за 2.1). Більше нічого в спеці не змінюється; розбіжності коду зі спекою перелічені в "Rulings" цього плану.

- [ ] **Step 4: Перевірити**

Run: `dotnet build NetSdr.sln --nologo -v q` без нових попереджень CS1573/CS1591; `dotnet test NetSdr.sln --nologo -v q`: усе зелене. Mermaid-блоки `architecture.md` перевірити рендером (наприклад, у переглядачі Markdown з Mermaid або `npx -y @mermaid-js/mermaid-cli -i docs/architecture.md -o /dev/null`, якщо доступно); інакше хоча б переконатися, що в `sequenceDiagram` немає `;` і кожен `opt`/`alt`/`loop` має свій `end`.

- [ ] **Step 5: Commit**

```bash
git add NetSdr/Control/ResilientControlClient.cs NetSdr/Control/ResilientControlClientOptions.cs docs/architecture.md docs/superpowers/specs/2026-10-07-netsdr-device-reboot-design.md
git commit -m "docs: ConnectAttempts and reboots in the XML docs, architecture diagrams and the spec status" -m "Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Rulings

Де спека мовчить або розходиться з кодом, план вирішує так (що, чому, ціна помилки).

1. Конструктор `RebootContext` публічний: тести Vega і будь-який застосунок, що тестує свій `IDeviceRebooter`, не бачать internal `NetSdr`. Ціна: ширша публічна поверхня на один конструктор.
2. `ReconnectState` отримує понад спеку 5.1 `LastFailure`, `ManualWaiters` і `Cause` стає `Exception?`: межа спроб потребує причини відмови без нової спроби, приєднані виклики завершуються після `Publish`, а перше підключення втрати не має. Ціна: лише внутрішня.
3. Межа `ReconnectAttempts`: `ShouldHandle` обох pipeline також не повторює при `state.Attempt >= межа`, а перевірка перед спробою перекидає `state.LastFailure`; `MaxRetryAttempts = межа - 1` Polly лишається. Ціна помилки: одна зайва або пропущена спроба на втрату.
4. `state.Attempt++` стоїть після підлоги 1 с, щоб пробудження під час підлоги не споживало номер спроби (спека 7.4 ставить його першим). Ціна: нумерація в 1104 зсунулась би на одиницю.
5. Невдача, що завершує серію (`Reboot`, `GiveUp`, вичерпана межа), не пише 1104/1118: про неї кажуть 1114, 1106 або кинутий виняток, як зараз для останньої спроби перед відмовою. Ціна: на одне попередження менше на серію.
6. Повідомлення `IOException` відмови за рішенням політики те саме, що для вичерпаних спроб (`Gave up reconnecting to {target} after {attempts} attempt(s).`); відрізняється лише `Reason` 1106. Ціна: застосунок розрізняє причини лише за логом.
7. `GetBootTime` понад `int.MaxValue` мс трактується як від'ємне: `InvalidOperationException`, 1115, перезавантаження зараховане. Ціна: немає; альтернатива це `ArgumentOutOfRangeException` із `Task.Delay` і хибна відмова.
8. `RebootContext.LastRemoteEndPoint` це `_link?.Client.RemoteEndPoint`: `null` протягом усього першого підключення, далі кінець останнього опублікованого з'єднання. Ціна: `VegaRebooter` без `Host` бере хост із `Target`, що той самий хост.
9. 1113 пишеться на кожен прийнятий виклик `RebootAsync`, зокрема злитий чи приєднаний. Ціна: більше записів, ніж перезавантажень; кількість перезавантажень дає 1116.
10. Виклик транспорту обгорнуто `.WaitAsync(linked.Token)`, покинута задача спостерігається: транспорт, що ігнорує токен, не тримає наглядача. Ціна: транспорт може працювати далі у фоні після таймауту.
11. `TakeRebootRequest` підміняє `_rebootWake` при кожному виклику, навіть із порожнім слотом; приєднання до запиту, що виконується, не скасовує його (спека 5.2 говорить лише про підміну при взятті запиту). Ціна: без цього хибне пробудження зациклює серію або серія після завантаження прокидається без запиту.
12. 1114 пишеться лише коли в крок не влився ручний запит; `Requested` істинний, коли влився. Ціна: ескалація, що збіглася з ручним запитом, виглядає як ручна.
13. `Silent` тестового сервера перериває обробку одразу після запису в `Received`, до `DispatchAsync`: обробники не виконуються, стан не змінюється. Ціна: тест, що хоче "мовчазний, але змінений стан", мусить це робити обробником.
14. Емулятор: `Hang(VegaHang.None)` знімає зависання і повертає `Normal`; зупинка UDP при `Hang` fire-and-forget; неправильно сформований сервісний запит не отримує відповіді й не записується. Ціна: лише тестова.
15. Спека 2.1 каже "події 1113-1117", розділи 4.5 і 8 додають 1118: план реалізує 1118 і виправляє 2.1 у задачі 11.
