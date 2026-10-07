# Архітектура NetSdr

Огляд у нотації [C4](https://c4model.com): контекст, контейнери, компоненти і дві
динамічні діаграми, що зшивають їх у робочі сценарії. Діаграми описують код у цьому
репозиторії. Деталі API і поведінки лежать у специфікаціях:

- [Фреймворк](superpowers/specs/2026-10-02-netsdr-framework-design.md): кадрування,
  структури команд, канали керування і даних, тестовий сервер, приклад Vega.
- [Ідентифікація пристрою](superpowers/specs/2026-10-02-netsdr-device-identification-design.md):
  паспорт, проби, каталог клієнтів, версії Vega.
- [Стійкість і логування](superpowers/specs/2026-10-07-netsdr-resilience-logging-design.md):
  `ResilientControlClient` з перепідключенням і heartbeat, `INetSdrControlClient`,
  логування всієї бібліотеки.

Рівень 4 (код) окремо не малюю. Класові діаграми ключових типів є в специфікаціях, а решту
швидше прочитати в самому коді.

**Нотація.** Діаграми це Mermaid `flowchart` у кольорах C4. Вбудований синтаксис C4 у
Mermaid досі експериментальний і розкладає блоки внахлест, тому він не використовується.

| Колір | Значення |
|---|---|
| Темно-синій | Людина |
| Синій | Система або контейнер у фокусі |
| Блакитний | Компонент |
| Сірий | Зовнішня система |
| Пунктир | Зв'язок, що існує лише в тестах |

## 1. Контекст

Хто користується системою і з чим вона говорить.

```mermaid
flowchart TB
    dev["<b>Розробник застосунку</b><br/>[Людина]<br/>Пише застосунок і його тести<br/>на фреймворку NetSdr"]
    app["<b>Застосунок SDR</b><br/>[Програмна система, .NET 10]<br/>Керує приймачем, приймає I/Q,<br/>записує або передає далі"]
    rx["<b>Приймач</b><br/>[Зовнішня система, залізо]<br/>SDR-IP, NetSDR або сумісний,<br/>наприклад Vega"]
    sink["<b>Споживач I/Q</b><br/>[Зовнішня система]<br/>Файл запису або наступний<br/>етап обробки"]
    emu["<b>NetSdrTestServer</b><br/>[Емулятор приймача]<br/>Замінює залізо в тестах"]

    dev -->|"пише код і тести"| app
    app -->|"Control Items: Set, Get<br/>[TCP 50000]"| rx
    rx -->|"відповіді, unsolicited<br/>[TCP 50000]"| app
    rx -->|"I/Q: Data Item 0<br/>[UDP, до 64 КБ]"| app
    app -->|"сирі пакети семплів"| sink
    app -.->|"той самий протокол<br/>[TCP + UDP, loopback]"| emu

    classDef person fill:#08427B,stroke:#052E56,color:#fff
    classDef system fill:#1168BD,stroke:#0B4884,color:#fff
    classDef external fill:#999999,stroke:#6B6B6B,color:#fff
    class dev person
    class app system
    class rx,sink,emu external
```

- Канали незалежні. TCP несе запит-відповідь і повідомлення, які пристрій надсилає сам.
  UDP несе лише потік I/Q від приймача до застосунку.
- Застосунок не декодує семпли: перший споживач записує сирі пакети.
- Пошук пристроїв broadcast-ом на порти 48321/48322 поза межами системи.

## 2. Контейнери

Застосунок працює одним процесом, тому контейнерами тут названо збірки .NET. Саме вони є
одиницями постачання, і межі між ними задаються посиланнями проєктів.

```mermaid
flowchart TB
    rx["<b>Приймач</b><br/>[Зовнішня система]"]

    subgraph ship["Постачається в застосунок"]
        app["<b>Застосунок</b><br/>[.NET 10, код користувача]<br/>Власні Control Items, проби,<br/>клієнти пристроїв"]
        vega["<b>NetSdr.Examples.Vega</b><br/>[Бібліотека-приклад]<br/>Протокол Vega поверх базового,<br/>клієнти прошивок v1 і v2"]
        core["<b>NetSdr</b><br/>[Бібліотека, Polly.Core,<br/>Logging.Abstractions]<br/>Кадрування, структури команд,<br/>TCP-клієнт, стійкий клієнт,<br/>UDP-приймач, ідентифікація"]
    end

    logs["<b>Провайдер логування</b><br/>[Зовнішня система, ILoggerFactory]<br/>Обирає застосунок:<br/>консоль, файл тощо"]

    subgraph testonly["Лише для тестів"]
        testing["<b>NetSdr.Testing</b><br/>[Бібліотека, без xUnit]<br/>NetSdrTestServer, ControlFrames,<br/>SampleSources, Eventually"]
        tests["<b>NetSdr.Tests</b><br/>[xUnit]<br/>Тести фреймворку"]
        vtests["<b>NetSdr.Examples.Vega.Tests</b><br/>[xUnit]<br/>Тести прикладу, VegaEmulator"]
    end

    app -->|"посилається,<br/>ILoggerFactory в опціях"| core
    vega -->|"посилається"| core
    testing -->|"посилається"| core
    tests -->|"посилається,<br/>InternalsVisibleTo"| core
    tests -->|"посилається"| testing
    vtests -->|"посилається"| vega
    vtests -->|"посилається"| testing
    core <-->|"TCP 50000 + UDP"| rx
    testing -.->|"емулює приймач<br/>[TCP + UDP, loopback]"| core
    core -->|"ILogger, EventId 1000-1399"| logs

    classDef container fill:#438DD5,stroke:#2E6295,color:#fff
    classDef external fill:#999999,stroke:#6B6B6B,color:#fff
    class app,vega,core,testing,tests,vtests container
    class rx,logs external
```

- `NetSdr` залежить від двох пакетів поза BCL. `Polly.Core` дає повтори і backoff
  усередині `ResilientControlClient`, у публічному API типів Polly немає.
  `Microsoft.Extensions.Logging.Abstractions` дає `ILoggerFactory` в опціях і генератор
  `[LoggerMessage]`. `System.IO.Pipelines` і `System.Threading.Channels` входять у runtime.
- Логування за замовчуванням вимкнене: `NullLoggerFactory`. `NetSdr.Testing` не логує.
- `NetSdr.Testing` окрема бібліотека без тестового фреймворку, тому тести застосунку
  беруть емулятор, не тягнучи xUnit у продакшн-код і не залежачи від тестів фреймворку.
- `NetSdr.Tests` бачить внутрішній метод клієнта, що приймає `PipeReader` і `Stream`.
  Так кадрування тестується на пам'яті, без сокетів. Так само він бачить internal-опції
  `TimeProvider`: backoff, відмова, `ResponseTimeout` і підсумки UDP тестуються на
  `FakeTimeProvider`, решта стійкого клієнта на справжньому часі з короткими таймаутами,
  логи на `FakeLogger` (пакети `Microsoft.Extensions.TimeProvider.Testing` і
  `Microsoft.Extensions.Diagnostics.Testing`).
- Приклад Vega показує, як будується застосунок: свої структури, своя проба, свої
  клієнти, свій емулятор поверх `NetSdrTestServer`.

## 3. Компоненти `NetSdr`

```mermaid
flowchart TB
    app["<b>Застосунок</b><br/>[Контейнер]"]
    rx["<b>Приймач</b><br/>[Зовнішня система]"]
    logs["<b>Провайдер логування</b><br/>[Зовнішня система, ILoggerFactory]"]

    subgraph core["NetSdr"]
        ident["<b>Identification</b><br/>[DeviceCatalog#lt;T#gt;, DeviceIdentity, Probes]<br/>Паспорт зі стандартних і власних проб,<br/>вибір клієнта за предикатами"]
        resil["<b>Resilience</b><br/>[ResilientControlClient]<br/>Наглядач: перепідключення з backoff,<br/>heartbeat Get 0x0005, ConnectionRestored,<br/>повтори команд через Polly"]
        control["<b>Control</b><br/>[NetSdrControlClient]<br/>Цикл читання на PipeReader,<br/>один запит у польоті під SemaphoreSlim,<br/>unsolicited в обмеженому Channel"]
        data["<b>Data</b><br/>[NetSdrDataReceiver, DataSequence]<br/>Окремий Thread, ReceiveFrom у буфер 64 КБ,<br/>контроль sequence, виклик callback"]
        items["<b>Items</b><br/>[IControlItem#lt;T#gt;, UInt40, 25 структур]<br/>Команда це struct,<br/>байти кастяться через MemoryMarshal"]
        framing["<b>Framing</b><br/>[FrameHeader, RequestType, ReplyType]<br/>Заголовок: 13 біт довжини, 3 біти типу"]
    end

    app -->|"Register, Default,<br/>ConnectAsync, AttachAsync"| ident
    app -->|"ConnectAsync, SetAsync,<br/>GetAsync, Unsolicited"| resil
    app -->|"SetAsync, GetAsync, Unsolicited"| control
    app -->|"Bind, Start, callback(info, samples)"| data
    app -.->|"власні структури команд"| items
    ident -->|"проби через INetSdrControlClient"| control
    ident -->|"проби через INetSdrControlClient"| resil
    resil -->|"новий клієнт на кожне з'єднання,<br/>сирий SendAsync"| control
    ident -->|"стандартні пункти 0x0001..0x000A"| items
    control -->|"T.Write, T.Read"| items
    control -->|"запис і розбір кадрів"| framing
    data -->|"перевірка заголовка"| framing
    control <-->|"TCP 50000"| rx
    rx -->|"UDP Data Item 0"| data
    control -->|"EventId 1000-1099"| logs
    resil -->|"EventId 1100-1199"| logs
    data -->|"EventId 1200-1299"| logs
    ident -->|"EventId 1300-1399"| logs

    classDef component fill:#85BBF0,stroke:#5D82A8,color:#000
    classDef container fill:#438DD5,stroke:#2E6295,color:#fff
    classDef external fill:#999999,stroke:#6B6B6B,color:#fff
    class ident,resil,control,data,items,framing component
    class app container
    class rx,logs external
```

- `Control` і `Data` не знають одне про одного. Застосунок з'єднує їх сам: бере
  `LocalEndPoint` приймача і передає його пристрою командою 0x00C5.
- `Identification` це надбудова над `Control`, а не частина клієнта. Паспортом можна
  користуватися без каталогу, а клієнтом без паспорта.
- `Resilience` теж надбудова над `Control`. На кожне з'єднання він створює новий
  `NetSdrControlClient`, і одночасно існує лише одне з'єднання. Обидва клієнти реалізують
  `INetSdrControlClient`, тому `Identification` і клієнти пристроїв працюють з будь-яким.
  Стан пристрою після перепідключення відновлює застосунок у `ConnectionRestored`.
- Логування наскрізне. Кожен компонент бере `ILoggerFactory` зі своїх опцій, категорія це
  повне ім'я класу, EventId стабільні й лежать у діапазоні компонента.
- `Items` не залежить ні від чого, крім BCL. Нова команда застосунку це нова структура,
  без реєстрації: код береться зі статичного члена `Code`.

### 3.1. Потоки виконання

Хто де виконується, бо від цього залежить, що можна робити в обробниках.

| Код | Потік | Наслідок |
|---|---|---|
| `SetAsync`, `GetAsync`, `SendAsync` | Потік виклику, далі пул | Запити стають у чергу на семафорі в порядку надходження |
| Цикл читання TCP | Задача пулу, одна на клієнт | Розбирає кадри і `T.Read` прямо з буфера pipe |
| Продовження після `await SetAsync` | Пул | `RunContinuationsAsynchronously`, код застосунку не блокує цикл читання |
| Читання `Unsolicited` | Будь-який, один читач | Канал обмежений, при переповненні викидається найстаріше |
| Наглядач `ResilientControlClient` | Задача пулу, одна на клієнт | Єдиний створює, перевіряє і публікує з'єднання. Чекає на `Completion` внутрішнього клієнта, перепідключається з backoff |
| Heartbeat | Усередині наглядача, затримки через `TimeProvider` | Get 0x0005 лише на вільній лінії, коли від пристрою нічого не чути `HeartbeatInterval`; новий не йде, поки попередній без відповіді |
| Callback `ConnectionRestored` | Наглядач, до публікації нового з'єднання | Не виконується паралельно сам із собою чи з heartbeat. Команди застосунку чекають, запити йдуть через `context.Client`; виклик самого `ResilientControlClient` звідси фатальний |
| Перекачування `Unsolicited` у `ResilientControlClient` | Задача пулу на кожне з'єднання | Один канал на весь час життя клієнта, повідомлення в порядку з'єднань |
| Callback `DataPacketHandler` | Виділений потік прийому UDP | Довга робота тут означає втрати в сокеті; span дійсний лише всередині виклику |
| Підсумок статистики UDP у лог | Той самий потік прийому, раз на `StatisticsLogInterval` | Таймера немає: годинник читається раз на 256 пакетів, окремі пакети не логуються |

## 4. Компоненти `NetSdr.Testing`

```mermaid
flowchart TB
    test["<b>Тест</b><br/>[xUnit]"]
    client["<b>NetSdrControlClient</b><br/>[NetSdr]"]
    recv["<b>NetSdrDataReceiver</b><br/>[NetSdr]"]

    subgraph testing["NetSdr.Testing"]
        server["<b>Керування</b><br/>[NetSdrTestServer]<br/>TcpListener на loopback,<br/>клієнти обслуговуються по одному,<br/>запити читаються з PipeReader"]
        disp["<b>Диспетчер</b><br/>[DispatchAsync]<br/>Обробник OnRequest,<br/>далі AutoStream для 0x0018,<br/>далі стан"]
        state["<b>Стан пристрою</b><br/>[код: список payload]<br/>Set зберігає і відлунює,<br/>Get віддає за ключем, Preload"]
        stream["<b>Потік даних</b><br/>[StreamPlan, DataStream]<br/>UDP-сокет, sequence за специфікацією,<br/>Pacing, DropPacket, jumbo"]
        sources["<b>Джерела семплів</b><br/>[SampleSources]<br/>Counter, Tone, FromBuffer"]
        frames["<b>ControlFrames</b><br/>Повні кадри для відповідей<br/>і порівняння в тестах"]
        eventually["<b>Eventually</b><br/>Очікування умови без sleep"]
    end

    test -->|"OnRequest, Preload, Received,<br/>SendUnsolicitedAsync"| server
    test -->|"порівнює байти"| frames
    test -->|"чекає стану"| eventually
    client <-->|"TCP loopback"| server
    server --> disp
    disp -->|"немає обробника"| state
    disp -->|"Run, Stop"| stream
    disp -->|"кодує відповідь"| frames
    stream -->|"FillSamples"| sources
    stream -->|"UDP loopback"| recv

    classDef component fill:#85BBF0,stroke:#5D82A8,color:#000
    classDef container fill:#438DD5,stroke:#2E6295,color:#fff
    class server,disp,state,stream,sources,frames,eventually component
    class test,client,recv container
```

- Порядок вибору відповіді: обробник для коду, потім `AutoStream` для 0x0018, потім стан.
  Тому перекриття в тесті завжди перемагає поведінку за замовчуванням.
- Параметри потоку беруться зі стану, який клієнт сам задав командами 0x00B8, 0x00C4,
  0x00C5 і 0x0018. Явні `StreamOptions` мають пріоритет.

## 5. Компоненти прикладу Vega

Як застосунок розширює фреймворк, нічого в ньому не змінюючи.

```mermaid
flowchart TB
    subgraph vega["NetSdr.Examples.Vega"]
        vitems["<b>Пункти Vega</b><br/>[VendorUnlock, AntennaSelect,<br/>BoardTemperatureV1, BoardTemperatureV2,<br/>DeviceLabel, OverloadEvent, VegaFirmwareInfo]<br/>Коди 0x8000..0x8005"]
        probe["<b>VegaProbes.Identify</b><br/>[ProbeAsync]<br/>Розблоковує, читає 0x8005,<br/>кладе факт VegaInfo"]
        base["<b>VegaReceiverBase</b><br/>Антени, мітка, потік, події,<br/>статичний ConnectAsync"]
        v1["<b>VegaV1Receiver</b><br/>Температура у форматі v1"]
        v2["<b>VegaV2Receiver</b><br/>Температура у форматі v2"]
    end

    subgraph core["NetSdr"]
        cat["<b>DeviceCatalog#lt;VegaReceiverBase#gt;</b>"]
        ctl["<b>INetSdrControlClient</b><br/>NetSdrControlClient або<br/>ResilientControlClient"]
        icontrol["<b>IControlItem#lt;T#gt;</b>"]
    end

    base -->|"ConnectAsync будує каталог"| cat
    cat -->|"виконує пробу"| probe
    cat -->|"правило Vega v2"| v2
    cat -->|"правило Vega v1"| v1
    v1 -->|"успадковує"| base
    v2 -->|"успадковує"| base
    probe -->|"SetAsync, GetAsync"| ctl
    base -->|"команди і Unsolicited"| ctl
    vitems -.->|"реалізують"| icontrol

    classDef component fill:#85BBF0,stroke:#5D82A8,color:#000
    classDef framework fill:#438DD5,stroke:#2E6295,color:#fff
    class vitems,probe,base,v1,v2 component
    class cat,ctl,icontrol framework
```

- Версія прошивки визначається власною командою після розблокування: NAK на 0x8005
  означає v1.
- Той самий код 0x8002 має дві структури. Версійні класи відрізняються лише читанням
  температури і розбором подій.

## 6. Динамічна діаграма: підключення і запис I/Q

Наскрізний сценарій через компоненти `NetSdr` зі звичайним `NetSdrControlClient`. Обрив і
відновлення з `ResilientControlClient` показано в розділі 7. Послідовності окремих частин
детальніше показано в специфікаціях.

```mermaid
sequenceDiagram
    autonumber
    participant App as Застосунок
    participant Cat as DeviceCatalog
    participant Id as DeviceIdentity
    participant CC as NetSdrControlClient
    participant DR as NetSdrDataReceiver
    participant Rx as Приймач

    App->>Cat: ConnectAsync(host)
    Cat->>CC: ConnectAsync
    CC->>Rx: TCP 50000
    Cat->>Id: ReadAsync(client, проби)
    Id->>CC: стандартні проби 0x0001..0x000A
    CC-->>Id: відповіді, NAK стає Unsupported
    Id->>CC: проби застосунку
    Id-->>Cat: паспорт
    Cat-->>App: пристрій: перший збіг або Default
    App->>DR: Bind(0), SetReceiveBuffer(200 мс, швидкість), Start
    App->>CC: SetAsync: частота дискретизації, частота, UDP-адреса приймача
    App->>CC: SetAsync(ReceiverState.Start)
    loop поки йде захоплення
        Rx-->>DR: UDP-пакет із sequence
        DR->>App: callback(info, samples) у потоці прийому
    end
    Rx-->>CC: unsolicited, наприклад перевантаження АЦП
    CC-->>App: Unsolicited channel
    App->>CC: SetAsync(ReceiverState.Stop)
    App->>DR: Dispose
    App->>CC: DisposeAsync
```

## 7. Динамічна діаграма: обрив і відновлення керування

Той самий застосунок із `ResilientControlClient`. Деталі в спеці
[стійкості і логування](superpowers/specs/2026-10-07-netsdr-resilience-logging-design.md).

```mermaid
sequenceDiagram
    autonumber
    participant App as Застосунок
    participant RC as ResilientControlClient
    participant Sup as Наглядач
    participant CC as NetSdrControlClient
    participant Rx as Приймач

    App->>RC: ConnectAsync(host, options із ConnectionRestored)
    RC->>CC: new, ConnectAsync
    CC->>Rx: TCP 50000, перевірка Get 0x0005
    RC->>Sup: старт
    App->>RC: catalog.AttachAsync(RC), далі команди
    alt пристрій закрив TCP
        Rx--xCC: обрив
    else лінія мовчить
        Sup->>CC: heartbeat Get 0x0005
        Note over Sup,CC: без відповіді ResponseTimeout + LateReplyTimeout, з'єднання закрито
    end
    CC-->>Sup: Completion завершено
    Note over RC,Sup: Warning у лог, нові команди чекають
    loop перша спроба одразу, далі 1, 2, 4 ... 30 с
        Sup->>CC: новий NetSdrControlClient, ConnectAsync
        CC->>Rx: TCP 50000, перевірка Get 0x0005
    end
    Sup->>App: ConnectionRestored(context, ct)
    App->>CC: context.Client: VendorUnlock, частоти, 0x00C5, ReceiverState.Start
    App-->>Sup: callback завершився
    Sup->>RC: Publish, Information у лог
    RC->>CC: команди, що чекали, у порядку викликів
    Note over App: розрив I/Q позначає context.LostAt
```

- Жодна команда застосунку не доходить до нового з'єднання, доки `ConnectionRestored` не
  повернувся. Команду, яку перервав обрив, клієнт надсилає знову вже на новому з'єднанні.
- Повільна відповідь це ще не обрив. Повтор чекає на запізнілу відповідь і не надсилає
  запит удруге. З'єднання замінюється, лише коли запит лишився без відповіді
  `ResponseTimeout + LateReplyTimeout`.
- `NetSdrDataReceiver` не змінюється і тримає свій порт. Пристрій після `ReceiverState.Start`
  починає sequence з 0, тому простій не потрапляє в `Lost`. Розрив застосунок позначає за
  `context.LostAt`.
