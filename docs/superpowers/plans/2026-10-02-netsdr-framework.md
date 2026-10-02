# NetSdr Framework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Бібліотека NetSdr для протоколу NetSDR/SDR-IP (TCP-керування, UDP-дані, структури команд), тестовий сервер `NetSdr.Testing` і приклад власного протоколу Vega з тестами на цьому сервері.

**Architecture:** Три незалежні компоненти ядра (`FrameHeader`, `NetSdrControlClient` на `PipeReader`, `NetSdrDataReceiver` на синхронному `ReceiveFrom` у власному потоці) плюс команди як `unmanaged`-структури з `static abstract`/`static virtual` членами інтерфейсу. Тестовий сервер в окремій бібліотеці емулює приймач через справжні сокети на loopback. Приклад Vega будує поверх ядра типізований клієнт і поверх тестового сервера емулятор свого пристрою.

**Tech Stack:** .NET 10 (SDK 10.0.401), C# latest, xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1, xunit.runner.visualstudio 3.1.4 (версії з шаблону `dotnet new xunit`), System.IO.Pipelines і System.Threading.Channels з BCL.

**Spec:** `docs/superpowers/specs/2026-10-02-netsdr-framework-design.md`; байтові приклади з `sdripinterfacespec103.pdf`.

## Global Constraints

- Усі проєкти `net10.0`, `Nullable` увімкнено, `ImplicitUsings` увімкнено, `AllowUnsafeBlocks` вимкнено: жодного `unsafe`, лише `MemoryMarshal`, `Unsafe`, `BinaryPrimitives` із BCL.
- `NetSdr` без `PackageReference`. `NetSdr.Testing` посилається лише на `NetSdr`, без xUnit.
- Файл рішення `NetSdr.sln` (формат `sln`, не `slnx`).
- Усі багатобайтові поля на дроті little-endian. Статичний конструктор `FrameHeader` кидає `PlatformNotSupportedException`, якщо `BitConverter.IsLittleEndian == false`.
- Структури команд: `[StructLayout(LayoutKind.Sequential, Pack = 1)]`, `readonly struct`, конструктор з усіма полями, публічні `readonly` поля в порядку, як у таблиці специфікації.
- Специфічні винятки фреймворку успадковують `NetSdrException`. BCL-винятки (`TimeoutException`, `OperationCanceledException`, `IOException`, `ObjectDisposedException`, `InvalidOperationException`, `ArgumentException`) використовуються там, де їх називає специфікація.
- Ідентифікатори, коментарі й тексти винятків англійською.
- Інтеграційні тести працюють лише на loopback із портом 0. Кожне очікування в тестах обмежене 5 секундами (`Limits.Test`), щоб тест падав, а не зависав.
- Кожне повідомлення коміту закінчується рядком `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Рішення поверх специфікації

План уточнює кілька місць, де специфікація мовчить або суперечить сама собі. Виконавець бере ці рішення як вимоги.

1. Винятки лежать у кореневому просторі імен `NetSdr`, бо їх кидають і `Framing`, і `Control`, і `Data`.
2. `FrameHeader.TryRead` повертає `false` і для короткого буфера, і для недійсного заголовка; клієнт розрізняє ці випадки, бо сам перевіряє, що байтів щонайменше 2. Додано константу `FrameHeader.MaxEncodableLength = 8191`.
3. `UInt40` з `ulong` понад `2^40 - 1` кидає `OverflowException`, а не обрізає.
4. Публічний помічник `NetSdr.Data.DataSequence` (`Next`, `Distance`) використовують і приймач, і тестовий сервер.
5. Датаграма з номером «позаду» очікуваного (відстань `>= 0x8000`) вважається переставленою: доставляється з `GapBefore = 0`, `Lost` і очікуваний номер не змінюються.
6. Запит, що завершився без відповіді через скасування або таймаут без `FaultOnTimeout`, запам'ятовується як покинутий. Перша відповідь із його кодом і типом іде в `Unsolicited` і не провалює наступний запит.
7. `Unsolicited` завершується без помилки і при `DisposeAsync`, і при збої; причину збою показує `Completion`.
8. `NetSdrControlClient` має `internal void Attach(PipeReader input, Stream output)` для тестів через in-memory `Pipe` (`InternalsVisibleTo NetSdr.Tests`).
9. Тестовий сервер зберігає до 16 останніх payload на код. Get повертає найновіший, що починається з ключа запиту. `OnRequest` замінює попередній обробник того самого коду. Виняток з обробника перетворюється на NAK.
10. `StreamOptions.Format`, `PayloadSize`, `SampleRate` стали nullable: `null` означає «зі стану пристрою або за замовчуванням», задане значення має пріоритет над станом (так реалізовано «явні StreamOptions мають пріоритет»).
11. `FillSamples` отримує формат: `delegate void FillSamples(Span<byte> destination, long firstSampleIndex, SampleFormat format)`. Тому `SampleSources.Tone` не має параметра формату.
12. `NetSdr.Testing` має ще два помічники: `ControlFrames.Decode<T>` і `Eventually.ThatAsync`.
13. `RfFilter.Filter` лишається `byte`, як у специфікації; властивість `Selection` повертає `RfFilterSelection`.

## Review Focus

П'ять вхідних ситуацій, які специфікація мається на увазі, але жоден її тест не перевіряє. Кожна має тест у задачі-власнику.

1. Дубльована або переставлена UDP-датаграма (номер позаду очікуваного) не повинна додати ~65 тисяч втрачених пакетів. Тест `Reordered_IsNotCountedAsLost`, задача 8.
2. Запізніла відповідь на скасований запит не повинна провалити наступний запит з іншим кодом. Тест `LateReplyOfAbandonedRequest_GoesToUnsolicited`, задача 6.
3. Частота понад 40 біт не повинна мовчки обрізатися. Тест `FromValueAbove40Bits_Throws`, задача 2.
4. Payload, що не вміщається в 13-бітну довжину, кидає `ArgumentOutOfRangeException` до запису в сокет, з'єднання лишається робочим. Тест `OversizedPayload_ThrowsBeforeWrite`, задача 5.
5. `Dispose` приймача з його ж callback не повинен зависати. Тест `Dispose_FromHandler_DoesNotDeadlock`, задача 8.

## File Structure

```
NetSdr.sln
Directory.Build.props                     спільні властивості збірки
NetSdr/
  NetSdr.csproj                           InternalsVisibleTo NetSdr.Tests
  NetSdrException.cs NetSdrProtocolException.cs NetSdrNakException.cs
  Framing/  FrameHeader.cs RequestType.cs ReplyType.cs
  Items/    IControlItem.cs UInt40.cs CaptureMode.cs RfFilterSelection.cs FrequencyRange.cs
            одна структура на файл: InterfaceVersion FirmwareVersion ProductId Options SecurityCode
            ReceiverState ReceiverChannelSetup ReceiverFrequency RfGain RfFilter AfGain AdModes
            AdInputSampleRate InputSyncMode PulseOutputMode OutputSampleRate DataOutputPacketSize
            DataOutputUdpAddress DcCalibration DacOutputMode
            TargetName SerialNumber StatusCodes FpgaConfiguration FrequencyRanges
  Control/  NetSdrControlClient.cs NetSdrControlClientOptions.cs ControlItemMessage.cs PendingRequest.cs
  Data/     NetSdrDataReceiver.cs DataReceiverOptions.cs DataPacketInfo.cs SampleFormat.cs
            DataRate.cs DataReceiverStatistics.cs DataSequence.cs
NetSdr.Testing/
  NetSdr.Testing.csproj
  ControlFrames.cs Eventually.cs ControlRequest.cs ControlReply.cs
  NetSdrTestServer.cs                     керування
  NetSdrTestServer.Streaming.cs           partial: потік даних
  StreamOptions.cs                        StreamOptions, Pacing, FillSamples
  SampleSources.cs
NetSdr.Tests/
  Hex.cs Limits.cs
  Framing/FrameHeaderTests.cs
  Items/ItemCodec.cs Items/MyVendorItem.cs Items/UInt40Tests.cs Items/ControlItemReadTests.cs
  Items/StandardItemTests.cs Items/VariableItemTests.cs
  Testing/ControlFramesTests.cs Testing/TestServerControlTests.cs Testing/TestServerStreamingTests.cs
  Testing/SampleSourcesTests.cs
  Control/PipeDevice.cs Control/ControlClientProtocolTests.cs Control/ControlClientLifecycleTests.cs
  Data/UdpTestSender.cs Data/PacketCollector.cs Data/DataSequenceTests.cs Data/DataRateTests.cs
  Data/DataReceiverTests.cs
  EndToEndTests.cs
examples/Vega/
  NetSdr.Examples.Vega/
    NetSdr.Examples.Vega.csproj VegaProtocol.cs VegaException.cs VegaEvent.cs VegaReceiver.cs
    Items/AntennaPort.cs TemperatureSensor.cs OverloadFlags.cs VendorUnlock.cs AntennaSelect.cs
          BoardTemperature.cs DeviceLabel.cs OverloadEvent.cs
  NetSdr.Examples.Vega.Tests/
    NetSdr.Examples.Vega.Tests.csproj Hex.cs Limits.cs VegaEmulator.cs VegaFixture.cs
    Items/VegaItemTests.cs
    Receiver/VegaConnectTests.cs Receiver/VegaCommandTests.cs Receiver/VegaEventTests.cs
    Receiver/VegaStreamTests.cs
```

Команда перевірки для всіх задач: `dotnet test NetSdr.sln --filter "FullyQualifiedName~<клас>"`; наприкінці кожної задачі `dotnet test NetSdr.sln` має пройти повністю.

---

### Task 1: Каркас рішення і кадрування

**Files:**
- Create: `NetSdr.sln`, `Directory.Build.props`, `NetSdr/NetSdr.csproj`, `NetSdr/NetSdrException.cs`, `NetSdr/NetSdrProtocolException.cs`, `NetSdr/NetSdrNakException.cs`, `NetSdr/Framing/RequestType.cs`, `NetSdr/Framing/ReplyType.cs`, `NetSdr/Framing/FrameHeader.cs`
- Create: `NetSdr.Tests/NetSdr.Tests.csproj`, `NetSdr.Tests/Hex.cs`, `NetSdr.Tests/Limits.cs`
- Test: `NetSdr.Tests/Framing/FrameHeaderTests.cs`

**Interfaces:**
- Produces:
  - `enum RequestType : byte { Set = 0, Get = 1, GetRange = 2, DataAck = 3, Data0 = 4, Data1 = 5, Data2 = 6, Data3 = 7 }` (`NetSdr.Framing`)
  - `enum ReplyType : byte { Response = 0, Unsolicited = 1, RangeResponse = 2, DataAck = 3, Data0 = 4, Data1 = 5, Data2 = 6, Data3 = 7 }`
  - `static class FrameHeader { const int Size = 2; const int MaxLength = 8194; const int MaxEncodableLength = 8191; static bool TryRead(ReadOnlySpan<byte> source, out int length, out byte type); static void Write(Span<byte> destination, int length, byte type); }`
  - `class NetSdrException : Exception` з конструкторами `(string message)` і `(string message, Exception? innerException)`; `sealed class NetSdrProtocolException : NetSdrException` з тими самими конструкторами; `sealed class NetSdrNakException : NetSdrException { NetSdrNakException(ushort code, RequestType requestType); ushort Code; RequestType RequestType; }`, повідомлення `$"Device rejected {requestType} of control item 0x{code:X4} (NAK)."`
  - Тести: `internal static class Hex { static byte[] Parse(string hex); }` (`Convert.FromHexString` без пробілів), `internal static class Limits { static readonly TimeSpan Test = TimeSpan.FromSeconds(5); }`

- [ ] **Step 1: Створити рішення і проєкти**

```bash
dotnet new sln -n NetSdr --format sln
dotnet new classlib -n NetSdr -o NetSdr
dotnet new xunit -n NetSdr.Tests -o NetSdr.Tests
rm NetSdr/Class1.cs NetSdr.Tests/UnitTest1.cs
dotnet sln NetSdr.sln add NetSdr/NetSdr.csproj NetSdr.Tests/NetSdr.Tests.csproj
dotnet add NetSdr.Tests/NetSdr.Tests.csproj reference NetSdr/NetSdr.csproj
```

`Directory.Build.props` задає `TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable`, `LangVersion latest`, `AllowUnsafeBlocks false`; ті самі властивості прибрати з csproj. У `NetSdr.csproj` додати `<ItemGroup><InternalsVisibleTo Include="NetSdr.Tests" /></ItemGroup>`.

- [ ] **Step 2: Написати тести, що падають**

```csharp
namespace NetSdr.Tests.Framing;

public class FrameHeaderTests
{
    [Theory]
    [InlineData(0, 4, "04 00")]
    [InlineData(1, 4, "04 20")]
    [InlineData(2, 5, "05 40")]
    [InlineData(3, 3, "03 60")]
    [InlineData(4, 1028, "04 84")]
    [InlineData(5, 1028, "04 A4")]
    [InlineData(6, 2, "02 C0")]
    [InlineData(7, 8191, "FF FF")]
    public void Write_ThenTryRead_RoundTripsAllTypes(byte type, int length, string hex)
    {
        var buffer = new byte[2];
        FrameHeader.Write(buffer, length, type);
        Assert.Equal(Hex.Parse(hex), buffer);
        Assert.True(FrameHeader.TryRead(buffer, out var readLength, out var readType));
        Assert.Equal(length, readLength);
        Assert.Equal(type, readType);
    }

    [Theory]
    [InlineData("00 80", 4)]
    [InlineData("00 A0", 5)]
    [InlineData("00 C0", 6)]
    [InlineData("00 E0", 7)]
    public void TryRead_ZeroLengthDataItem_Means8194(string hex, byte type)
    {
        Assert.True(FrameHeader.TryRead(Hex.Parse(hex), out var length, out var readType));
        Assert.Equal(8194, length);
        Assert.Equal(type, readType);
    }

    [Theory]
    [InlineData("00 00")]
    [InlineData("01 00")]
    [InlineData("00 20")]
    [InlineData("00 60")]
    [InlineData("01 80")]
    public void TryRead_LengthBelowTwo_IsInvalid(string hex) =>
        Assert.False(FrameHeader.TryRead(Hex.Parse(hex), out _, out _));

    [Fact]
    public void TryRead_OneByte_ReturnsFalse() => Assert.False(FrameHeader.TryRead([0x04], out _, out _));

    [Fact]
    public void TryRead_BoundaryLengths()
    {
        Assert.True(FrameHeader.TryRead(Hex.Parse("02 00"), out var min, out _));
        Assert.Equal(2, min);
        Assert.True(FrameHeader.TryRead(Hex.Parse("FF 1F"), out var max, out var type));
        Assert.Equal(8191, max);
        Assert.Equal(0, type);
    }

    [Fact]
    public void Write_8194ForDataItem_EncodesZero()
    {
        var buffer = new byte[2];
        FrameHeader.Write(buffer, 8194, 4);
        Assert.Equal(Hex.Parse("00 80"), buffer);
    }

    [Theory]
    [InlineData(8194, 0)]
    [InlineData(1, 0)]
    [InlineData(8192, 4)]
    [InlineData(4, 8)]
    public void Write_InvalidArguments_Throw(int length, byte type) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameHeader.Write(new byte[2], length, type));
}
```

- [ ] **Step 3: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~FrameHeaderTests"`
Expected: збірка падає, `FrameHeader` не існує.

- [ ] **Step 4: Реалізувати переліки, винятки і `FrameHeader`**

Заголовок `raw = (type << 13) | (length & 0x1FFF)`, little-endian. `TryRead`: менше 2 байтів дає `false`; якщо 13-бітна довжина 0 і `type >= 4`, довжина 8194; інакше довжина менша за 2 дає `false`. `Write`: `type > 7` або довжина поза `2..8191` кидають `ArgumentOutOfRangeException`, виняток становить `length == 8194` для `type >= 4`, що записується як 0. Статичний конструктор перевіряє порядок байтів (Global Constraints).

- [ ] **Step 5: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~FrameHeaderTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: solution scaffold and frame header" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Інтерфейс команд, `UInt40` і `ControlFrames`

**Files:**
- Create: `NetSdr/Items/IControlItem.cs`, `NetSdr/Items/UInt40.cs`
- Create: `NetSdr.Testing/NetSdr.Testing.csproj`, `NetSdr.Testing/ControlFrames.cs`, `NetSdr.Testing/Eventually.cs`
- Create: `NetSdr.Tests/Items/ItemCodec.cs`, `NetSdr.Tests/Items/MyVendorItem.cs`
- Test: `NetSdr.Tests/Items/UInt40Tests.cs`, `NetSdr.Tests/Items/ControlItemReadTests.cs`, `NetSdr.Tests/Testing/ControlFramesTests.cs`

**Interfaces:**
- Consumes: `FrameHeader`, `RequestType`, `ReplyType` (Task 1).
- Produces:
  - `interface IControlItem<TSelf> where TSelf : struct, IControlItem<TSelf>` точно як у специфікації 4.1: `static abstract ushort Code { get; }`, `static virtual int GetSize(in TSelf item) => Unsafe.SizeOf<TSelf>()`, `static virtual void Write(in TSelf item, Span<byte> destination) => MemoryMarshal.Write(destination, in item)`, `static virtual TSelf Read(ReadOnlySpan<byte> source) => MemoryMarshal.Read<TSelf>(source)`.
  - `readonly struct UInt40 : IEquatable<UInt40>` (Pack = 1, п'ять полів `byte`): `const ulong MaxValue = (1UL << 40) - 1`, `UInt40(ulong value)`, `implicit operator ulong(UInt40)`, `implicit operator UInt40(ulong)`, `ToString()` як десяткове значення.
  - `static class ControlFrames` (`NetSdr.Testing`): `byte[] Encode(byte type, ushort code, ReadOnlySpan<byte> payload)`, `byte[] Request<T>(RequestType type, in T item)`, `byte[] Reply<T>(ReplyType type, in T item)`, `T Decode<T>(ReadOnlySpan<byte> frame)`; `T : struct, IControlItem<T>`.
  - `static class Eventually` (`NetSdr.Testing`): `Task ThatAsync(Func<bool> condition, TimeSpan? timeout = null)`. Перевіряє умову кожні 10 мс; через `timeout` (за замовчуванням 5 с) кидає `TimeoutException`.
  - Тести: `ItemCodec.Read<T>(byte[] payload)` викликає `T.Read`, `ItemCodec.Write<T>(in T item)` повертає `byte[]` розміру `T.GetSize`; `MyVendorItem` зі специфікації 4.3 (код 0x0150, `byte Channel`, `uint Value`).

- [ ] **Step 1: Створити `NetSdr.Testing`**

```bash
dotnet new classlib -n NetSdr.Testing -o NetSdr.Testing
rm NetSdr.Testing/Class1.cs
dotnet sln NetSdr.sln add NetSdr.Testing/NetSdr.Testing.csproj
dotnet add NetSdr.Testing/NetSdr.Testing.csproj reference NetSdr/NetSdr.csproj
dotnet add NetSdr.Tests/NetSdr.Tests.csproj reference NetSdr.Testing/NetSdr.Testing.csproj
```

- [ ] **Step 2: Написати тести, що падають**

```csharp
// NetSdr.Tests/Items/UInt40Tests.cs
public class UInt40Tests
{
    [Theory]
    [InlineData(0UL, "00 00 00 00 00")]
    [InlineData(14_010_000UL, "90 C6 D5 00 00")]
    [InlineData(7_123_456_789UL, "15 53 97 A8 01")]
    [InlineData(UInt40.MaxValue, "FF FF FF FF FF")]
    public void Layout_IsFiveBytesLittleEndian(ulong value, string hex)
    {
        UInt40 v = value;
        Assert.Equal(5, Unsafe.SizeOf<UInt40>());
        Assert.Equal(Hex.Parse(hex), MemoryMarshal.AsBytes(new ReadOnlySpan<UInt40>(in v)).ToArray());
        Assert.Equal(value, (ulong)v);
    }

    [Fact]
    public void FromValueAbove40Bits_Throws() =>
        Assert.Throws<OverflowException>(() => { UInt40 v = UInt40.MaxValue + 1; });
}

// NetSdr.Tests/Items/ControlItemReadTests.cs
public class ControlItemReadTests
{
    [Fact]
    public void DefaultRead_ShortSource_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemCodec.Read<MyVendorItem>(Hex.Parse("00 2A 00")));

    [Fact]
    public void DefaultRead_LongerSource_ReadsPrefix()
    {
        var item = ItemCodec.Read<MyVendorItem>(Hex.Parse("07 2A 00 00 00 FF FF"));
        Assert.Equal(7, item.Channel);
        Assert.Equal(42u, item.Value);
    }
}

// NetSdr.Tests/Testing/ControlFramesTests.cs
public class ControlFramesTests
{
    [Fact]
    public void Request_Set_BuildsHeaderCodeAndPayload() =>
        Assert.Equal(Hex.Parse("09 00 50 01 00 2A 00 00 00"),
            ControlFrames.Request(RequestType.Set, new MyVendorItem(0, 42)));

    [Fact]
    public void Reply_Unsolicited_UsesType1() =>
        Assert.Equal(Hex.Parse("09 20 50 01 01 2A 00 00 00"),
            ControlFrames.Reply(ReplyType.Unsolicited, new MyVendorItem(1, 42)));

    [Fact]
    public void Encode_WithoutPayload() =>
        Assert.Equal(Hex.Parse("04 20 01 00"), ControlFrames.Encode((byte)RequestType.Get, 0x0001, []));

    [Fact]
    public void Decode_ReadsItem()
    {
        var item = ControlFrames.Decode<MyVendorItem>(Hex.Parse("09 00 50 01 03 2A 00 00 00"));
        Assert.Equal(3, item.Channel);
        Assert.Equal(42u, item.Value);
    }

    [Theory]
    [InlineData("09 00 51 01 03 2A 00 00 00")]
    [InlineData("0A 00 50 01 03 2A 00 00 00")]
    public void Decode_WrongCodeOrLength_Throws(string hex) =>
        Assert.Throws<ArgumentException>(() => ControlFrames.Decode<MyVendorItem>(Hex.Parse(hex)));

    [Fact]
    public async Task Eventually_TimesOut() =>
        await Assert.ThrowsAsync<TimeoutException>(() => Eventually.ThatAsync(() => false, TimeSpan.FromMilliseconds(50)));
}
```

- [ ] **Step 3: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~UInt40Tests|FullyQualifiedName~ControlItemReadTests|FullyQualifiedName~ControlFramesTests"`
Expected: збірка падає, типів немає.

- [ ] **Step 4: Реалізувати `IControlItem<TSelf>`, `UInt40`, `ControlFrames`, `Eventually` і тестові `ItemCodec`, `MyVendorItem`**

`ControlFrames.Encode` пише заголовок через `FrameHeader.Write(4 + payload.Length, type)`, код через `BinaryPrimitives`, потім payload. `Request` і `Reply` пишуть payload через `T.GetSize` і `T.Write` прямо в кадр. `Decode` кидає `ArgumentException`, якщо заголовок недійсний, довжина з заголовка не дорівнює `frame.Length` або код не `T.Code`; інакше повертає `T.Read(frame[4..])` і тип кадру не перевіряє.

- [ ] **Step 5: Запустити тести**

Run: та сама команда, що в Step 3.
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: control item interface, UInt40 and test frame helpers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Стандартні структури фіксованого розміру

**Files:**
- Create: `NetSdr/Items/` по одному файлу на кожну структуру з таблиці нижче, плюс `CaptureMode.cs`, `RfFilterSelection.cs`
- Test: `NetSdr.Tests/Items/StandardItemTests.cs`

**Interfaces:**
- Consumes: `IControlItem<T>`, `UInt40`, `ControlFrames` (Task 2).
- Produces (усі в `NetSdr.Items`, поля в цьому порядку, конструктор з усіма полями в тому ж порядку):

| Структура | Code | Поля | Додатково |
|---|---|---|---|
| `InterfaceVersion` | 0x0003 | `ushort Version` | |
| `FirmwareVersion` | 0x0004 | `byte Id`, `ushort Version` | `byte FpgaConfigId => (byte)Version`, `byte FpgaRevision => (byte)(Version >> 8)` |
| `ProductId` | 0x0009 | `uint Value` | |
| `Options` | 0x000A | `byte Flags`, `byte Custom`, `uint Detail` | `const byte ReflockBoard = 0x02, DownConverterBoard = 0x04` |
| `SecurityCode` | 0x000B | `uint Value` | |
| `ReceiverState` | 0x0018 | `byte DataType`, `byte Run`, `byte CaptureMode`, `byte FifoBlocks` | `const byte Idle = 0x01, Running = 0x02`; `static ReceiverState Start(bool complex, bool bits24, CaptureMode mode = CaptureMode.Contiguous, byte fifoBlocks = 0)`; `static ReceiverState Stop { get; }`; `bool IsRunning`, `bool IsComplex`, `bool Is24Bit` |
| `ReceiverChannelSetup` | 0x0019 | `byte Mode` | `const byte SingleChannel = 0, DualChannelSingleAd = 4` |
| `ReceiverFrequency` | 0x0020 | `byte Channel`, `UInt40 Hz` | `const byte Channel1 = 0, Display = 1, Channel2 = 2, All = 0xFF` |
| `RfGain` | 0x0038 | `byte Channel`, `sbyte GainDb` | |
| `RfFilter` | 0x0044 | `byte Channel`, `byte Filter` | конструктор `(byte channel, RfFilterSelection filter)`; `RfFilterSelection Selection => (RfFilterSelection)Filter` |
| `AfGain` | 0x0048 | `byte Channel`, `byte Level` | |
| `AdModes` | 0x008A | `byte Channel`, `byte Flags` | `const byte Dither = 0x01, Gain1_5 = 0x02` |
| `AdInputSampleRate` | 0x00B0 | `byte Channel`, `uint Hz` | |
| `InputSyncMode` | 0x00B4 | `byte Channel`, `byte Mode`, `ushort PacketCount` | `const byte None = 0, NegativeEdgeStart = 1, PositiveEdgeStart = 2, LowLevelStart = 3, HighLevelStart = 4, LowLevelMute = 5, HighLevelMute = 6` |
| `PulseOutputMode` | 0x00B6 | `byte Channel`, `byte Mode` | `const byte None = 0, RunState = 1, RunPulse = 2, SampleRate = 3` |
| `OutputSampleRate` | 0x00B8 | `byte Channel`, `uint Hz` | |
| `DataOutputPacketSize` | 0x00C4 | `byte Size` | `const byte Large = 0, Small = 1` |
| `DataOutputUdpAddress` | 0x00C5 | `uint Ip`, `ushort Port` | `static DataOutputUdpAddress For(IPEndPoint endPoint)` (лише IPv4, інакше `ArgumentException`); `IPEndPoint ToEndPoint()`. `Ip` це байти адреси як big-endian `uint`: 192.168.3.123 дає `0xC0A8037B` |
| `DcCalibration` | 0x00D0 | `byte Channel`, `short Offset` | |
| `DacOutputMode` | 0x012A | `byte Channel`, `byte Mode` | `const byte None = 0, AdEcho = 1, NcoTrack = 2, Noise = 3` |

  - `enum CaptureMode : byte { Contiguous = 0, Fifo = 1, HardwareTriggered = 3 }`. `Start` дає `DataType = complex ? 0x80 : 0`, `Run = Running`, `CaptureMode = (bits24 ? 0x80 : 0) | mode`. `Stop` дає `00 01 00 00`. Усередині `ReceiverState` перелік треба писати повним ім'ям `NetSdr.Items.CaptureMode`, бо поле називається так само.
  - `enum RfFilterSelection : byte { Auto = 0, Band0To1_8 = 1, Band1_8To2_8 = 2, Band2_8To4 = 3, Band4To5_5 = 4, Band5_5To7 = 5, Band7To10 = 6, Band10To14 = 7, Band14To20 = 8, Band20To28 = 9, Band28To34 = 10, Bypass = 11, NoPass = 12, DownConverter = 13 }`.

- [ ] **Step 1: Написати тести, що падають**

Байти з PDF, розділи 4.1 до 4.4. У прикладі Options у PDF помилка в довжині (`08` замість `0A`); тут правильна довжина.

```csharp
public class StandardItemTests
{
    static void AssertSetFrame<T>(T item, string hex) where T : struct, IControlItem<T>
    {
        var frame = ControlFrames.Request(RequestType.Set, item);
        Assert.Equal(Hex.Parse(hex), frame);
        Assert.Equal(item, ControlFrames.Decode<T>(frame));
    }

    [Fact] public void ReceiverFrequency_14_010MHz() =>
        AssertSetFrame(new ReceiverFrequency(ReceiverFrequency.Channel1, 14_010_000), "0A 00 20 00 00 90 C6 D5 00 00");
    [Fact] public void ReceiverFrequency_Display_7_123456789GHz() =>
        AssertSetFrame(new ReceiverFrequency(ReceiverFrequency.Display, 7_123_456_789), "0A 00 20 00 01 15 53 97 A8 01");
    [Fact] public void RfGain_Minus20() => AssertSetFrame(new RfGain(0, -20), "06 00 38 00 00 EC");
    [Fact] public void AfGain_10() => AssertSetFrame(new AfGain(0, 10), "06 00 48 00 00 0A");
    [Fact] public void RfFilter_5_5To7() => AssertSetFrame(new RfFilter(0, RfFilterSelection.Band5_5To7), "06 00 44 00 00 05");
    [Fact] public void AdModes_DitherAndGain() =>
        AssertSetFrame(new AdModes(0, AdModes.Dither | AdModes.Gain1_5), "06 00 8A 00 00 03");
    [Fact] public void InputSyncMode_NegativeEdge1000() =>
        AssertSetFrame(new InputSyncMode(0, InputSyncMode.NegativeEdgeStart, 1000), "08 00 B4 00 00 01 E8 03");
    [Fact] public void OutputSampleRate_500k() => AssertSetFrame(new OutputSampleRate(0, 500_000), "09 00 B8 00 00 20 A1 07 00");
    [Fact] public void AdInputSampleRate_80_000_123() =>
        AssertSetFrame(new AdInputSampleRate(0, 80_000_123), "09 00 B0 00 00 7B B4 C4 04");
    [Fact] public void DcCalibration_Minus234() => AssertSetFrame(new DcCalibration(0, -234), "07 00 D0 00 00 16 FF");
    [Fact] public void PulseOutputMode_SampleRate() =>
        AssertSetFrame(new PulseOutputMode(0, PulseOutputMode.SampleRate), "06 00 B6 00 00 03");
    [Fact] public void DacOutputMode_NcoTrack() => AssertSetFrame(new DacOutputMode(0, DacOutputMode.NcoTrack), "06 00 2A 01 00 02");
    [Fact] public void DataOutputPacketSize_Small() =>
        AssertSetFrame(new DataOutputPacketSize(DataOutputPacketSize.Small), "05 00 C4 00 01");
    [Fact] public void ReceiverChannelSetup_Dual() =>
        AssertSetFrame(new ReceiverChannelSetup(ReceiverChannelSetup.DualChannelSingleAd), "05 00 19 00 04");
    [Fact] public void ReceiverState_Start24BitComplex() =>
        AssertSetFrame(ReceiverState.Start(complex: true, bits24: true), "08 00 18 00 80 02 80 00");
    [Fact] public void ReceiverState_Stop() => AssertSetFrame(ReceiverState.Stop, "08 00 18 00 00 01 00 00");
    [Fact] public void ReceiverState_FifoAndTriggered()
    {
        AssertSetFrame(ReceiverState.Start(false, false, CaptureMode.Fifo, 4), "08 00 18 00 00 02 01 04");
        var triggered = ReceiverState.Start(true, true, CaptureMode.HardwareTriggered);
        Assert.Equal(0x83, triggered.CaptureMode);
        Assert.True(triggered.IsRunning && triggered.IsComplex && triggered.Is24Bit);
    }

    [Fact]
    public void DataOutputUdpAddress_FromEndPoint()
    {
        var endPoint = new IPEndPoint(IPAddress.Parse("192.168.3.123"), 12345);
        var item = DataOutputUdpAddress.For(endPoint);
        AssertSetFrame(item, "0A 00 C5 00 7B 03 A8 C0 39 30");
        Assert.Equal(endPoint, item.ToEndPoint());
        Assert.Throws<ArgumentException>(() => DataOutputUdpAddress.For(new IPEndPoint(IPAddress.IPv6Loopback, 1)));
    }

    [Fact] public void InterfaceVersion_529() =>
        Assert.Equal(529, ControlFrames.Decode<InterfaceVersion>(Hex.Parse("06 00 03 00 11 02")).Version);

    [Fact]
    public void FirmwareVersion_AppAndFpga()
    {
        var app = ControlFrames.Decode<FirmwareVersion>(Hex.Parse("07 00 04 00 01 11 02"));
        Assert.Equal((1, 529), (app.Id, app.Version));
        var fpga = ControlFrames.Decode<FirmwareVersion>(Hex.Parse("07 00 04 00 03 03 1C"));
        Assert.Equal((3, 28), (fpga.FpgaConfigId, fpga.FpgaRevision));
    }

    [Fact] public void ProductId_SdrIp() =>
        Assert.Equal(0x03524453u, ControlFrames.Decode<ProductId>(Hex.Parse("08 00 09 00 53 44 52 03")).Value);

    [Fact]
    public void Options_Reflock()
    {
        var options = ControlFrames.Decode<Options>(Hex.Parse("0A 00 0A 00 02 00 00 00 00 00"));
        Assert.Equal((Options.ReflockBoard, 0, 0u), (options.Flags, options.Custom, options.Detail));
    }

    [Fact] public void SecurityCode_Value() =>
        Assert.Equal(0xDEADBEEFu, ControlFrames.Decode<SecurityCode>(Hex.Parse("08 00 0B 00 EF BE AD DE")).Value);
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~StandardItemTests"`
Expected: збірка падає, структур немає.

- [ ] **Step 3: Реалізувати 20 структур і два переліки за таблицею з Interfaces**

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~StandardItemTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: standard fixed-size control items" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Структури змінної довжини

**Files:**
- Create: `NetSdr/Items/TargetName.cs`, `SerialNumber.cs`, `StatusCodes.cs`, `FpgaConfiguration.cs`, `FrequencyRanges.cs`, `FrequencyRange.cs`
- Test: `NetSdr.Tests/Items/VariableItemTests.cs`

**Interfaces:**
- Consumes: `IControlItem<T>`, `UInt40`, `ControlFrames`, `ItemCodec`.
- Produces:
  - `readonly struct TargetName` (0x0001) і `SerialNumber` (0x0002): `string Value` (`default` дає `""`), конструктор `(string value)`, власний `static Read`: ASCII до першого нуля або до кінця. `Write` не перевизначають, тому запис кидає `ArgumentException`.
  - `readonly struct StatusCodes` (0x0005): `byte[] Codes` (усі байти payload), `const byte Idle = 0x0B, Busy = 0x0C, LoadingParameters = 0x0D, BootIdle = 0x0E, BootBusy = 0x0F, AdOverload = 0x20, BootError = 0x80`.
  - `readonly struct FpgaConfiguration` (0x000C): `byte Selected`, `byte Id`, `byte Revision`, `string Description`; конструктори `(byte selected)` для Set і `(byte selected, byte id, byte revision, string description)`; `GetSize` дає 1, `Write` пише лише `Selected`, `Read` читає три байти і рядок до нуля.
  - `readonly record struct FrequencyRange(ulong Min, ulong Max, ulong Vco)`.
  - `readonly struct FrequencyRanges` (0x0020): `byte Channel`, `FrequencyRange[] Ranges`; `Read`: `[channel][count]`, далі `count` записів по 15 байтів (три `UInt40`). Payload коротший за `2 + count * 15` кидає `ArgumentException`.

- [ ] **Step 1: Написати тести, що падають**

У PDF приклад FPGA має код 0x0A; правильний код 0x000C.

```csharp
public class VariableItemTests
{
    [Fact]
    public void TargetName_SdrIp()
    {
        Assert.Equal("SDR-IP", ControlFrames.Decode<TargetName>(Hex.Parse("0B 00 01 00 53 44 52 2D 49 50 00")).Value);
        Assert.Equal("SDR-IP", ItemCodec.Read<TargetName>(Hex.Parse("53 44 52 2D 49 50")).Value);
        Assert.Equal("", default(TargetName).Value);
        Assert.Throws<ArgumentException>(() => ItemCodec.Write(new TargetName("x")));
    }

    [Fact] public void SerialNumber_MT123456() =>
        Assert.Equal("MT123456",
            ControlFrames.Decode<SerialNumber>(Hex.Parse("0D 00 02 00 4D 54 31 32 33 34 35 36 00")).Value);

    [Fact]
    public void StatusCodes_IdleAndOverload()
    {
        Assert.Equal(new[] { StatusCodes.Idle }, ControlFrames.Decode<StatusCodes>(Hex.Parse("05 00 05 00 0B")).Codes);
        Assert.Equal(new[] { StatusCodes.AdOverload }, ControlFrames.Decode<StatusCodes>(Hex.Parse("05 20 05 00 20")).Codes);
    }

    [Fact]
    public void FpgaConfiguration_ReadAndSet()
    {
        var config = ControlFrames.Decode<FpgaConfiguration>(Hex.Parse(
            "18 00 0C 00 01 02 09 53 74 64 20 46 50 47 41 20 43 6F 6E 66 69 67 20 00"));
        Assert.Equal((1, 2, 9, "Std FPGA Config "), (config.Selected, config.Id, config.Revision, config.Description));
        Assert.Equal(Hex.Parse("05 00 0C 00 02"), ControlFrames.Request(RequestType.Set, new FpgaConfiguration(2)));
    }

    const string RangesFrame =
        "24 40 20 00 00 02 A0 86 01 00 00 80 CC 06 02 00 00 00 00 00 00 " +
        "00 3B 58 08 00 80 D1 F0 08 00 00 68 89 09 00";

    [Fact]
    public void FrequencyRanges_TwoBands()
    {
        var ranges = ControlFrames.Decode<FrequencyRanges>(Hex.Parse(RangesFrame));
        Assert.Equal(0, ranges.Channel);
        Assert.Equal(
            new[] { new FrequencyRange(100_000, 34_000_000, 0), new FrequencyRange(140_000_000, 150_000_000, 160_000_000) },
            ranges.Ranges);
    }

    [Fact]
    public void FrequencyRanges_Truncated_Throws() =>
        Assert.Throws<ArgumentException>(() => ItemCodec.Read<FrequencyRanges>(Hex.Parse(RangesFrame)[4..21]));
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~VariableItemTests"`
Expected: збірка падає.

- [ ] **Step 3: Реалізувати шість типів за Interfaces**

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~VariableItemTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: variable-length control items" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Клієнт керування: кадрування і зіставлення відповідей

**Files:**
- Create: `NetSdr/Control/NetSdrControlClient.cs`, `NetSdrControlClientOptions.cs`, `ControlItemMessage.cs`, `PendingRequest.cs`
- Create: `NetSdr.Tests/Control/PipeDevice.cs`
- Test: `NetSdr.Tests/Control/ControlClientProtocolTests.cs`

**Interfaces:**
- Consumes: `FrameHeader`, `RequestType`, `ReplyType`, винятки (Task 1); `IControlItem<T>` (Task 2); структури (Tasks 3, 4).
- Produces: публічне API специфікації 5.1 без змін, а також:
  - `ControlItemMessage(ReplyType type, ushort code, ReadOnlyMemory<byte> payload)` як публічний конструктор. `As<T>` загортає будь-який виняток `T.Read` у `NetSdrProtocolException`.
  - `internal void Attach(PipeReader input, Stream output)` запускає цикл читання; `IsConnected` після нього `true`.
  - `internal abstract class PendingRequest` (`Code`, `ExpectedType`, `RequestType`, `abstract void Complete(ReadOnlySpan<byte> payload)`, `abstract void Fail(Exception exception)`); нащадки `PendingRequest<T>` з `TaskCompletionSource<T>` і `PendingRawRequest` з `TaskCompletionSource<ControlItemMessage>`, обидва з `RunContinuationsAsynchronously`. Збій `T.Read` у `Complete` провалює запит `NetSdrProtocolException`.
  - Тести: `sealed class PipeDevice : IAsyncDisposable` зі `static PipeDevice Create(NetSdrControlClientOptions? options = null, PipeOptions? toClient = null)`, `NetSdrControlClient Client`, `Task<byte[]> ReadRequestAsync()` (рівно один кадр від клієнта, обмежено `Limits.Test`), `Task SendAsync(string hex)`, `Task SendBytewiseAsync(string hex)` (по байту з flush), `void CloseRemote()` (завершує writer у бік клієнта). Клієнт підключено через `Attach(toClient.Reader, fromClient.Writer.AsStream())`.

Правила, які тести нижче фіксують, а реалізація має виконати:
- Кадр запиту: заголовок, код і payload в одному буфері з `ArrayPool`, один `WriteAsync`. Довжина понад 8191 кидає `ArgumentOutOfRangeException` до семафора і до запису. Запис іде з `CancellationToken.None`, щоб скасування не залишило пів кадру.
- Payload для `GetAsync<T, TKey>` це `MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key))`. Set і Get чекають `Response`, GetRange чекає `RangeResponse`, код завжди `T.Code`.
- `SendAsync` приймає лише `Set`, `Get`, `GetRange`; інші типи кидають `ArgumentOutOfRangeException`.
- Цикл читання за схемою 5.2 специфікації. Для типів 0..2 довжина 3 означає зламаний кадр і зупиняє з'єднання. Довжина 2 у `Response` це NAK; за активного запиту він провалює його `NetSdrNakException(pending.Code, pending.RequestType)`, без запиту йде в `Unsolicited` з кодом 0. Довжина 2 у типах 1 і 2 теж іде в `Unsolicited` з кодом 0. `DataAck` і `Data0..3` йдуть в `Unsolicited` з кодом 0 і payload, тобто всім після заголовка. Payload, що лежить у кількох сегментах, збирається в орендований буфер.
- Недійсний заголовок і закриття потоку з боку пристрою переводять клієнт у стан збою: активний запит провалюється тим самим винятком, `Unsolicited` завершується без помилки, `Completion` завершується винятком, `IsConnected` стає `false`. Закриття з боку пристрою дає `IOException("Connection closed by the device.")`.
- `Unsolicited`: `Channel.CreateBounded` з `UnsolicitedCapacity` і `BoundedChannelFullMode.DropOldest`.

- [ ] **Step 1: Написати `PipeDevice` і тести, що падають**

```csharp
public class ControlClientProtocolTests
{
    const string Freq14 = "0A 00 20 00 00 90 C6 D5 00 00";
    const string RangesFrame =
        "24 40 20 00 00 02 A0 86 01 00 00 80 CC 06 02 00 00 00 00 00 00 " +
        "00 3B 58 08 00 80 D1 F0 08 00 00 68 89 09 00";

    [Fact]
    public async Task SetAsync_SendsFrameAndReturnsEcho()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        Assert.Equal(Hex.Parse(Freq14), await device.ReadRequestAsync());
        await device.SendAsync(Freq14);
        Assert.Equal(14_010_000UL, (ulong)(await call.WaitAsync(Limits.Test)).Hz);
    }

    [Fact]
    public async Task GetAsync_WithByteKey()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        Assert.Equal(Hex.Parse("05 20 20 00 00"), await device.ReadRequestAsync());
        await device.SendAsync(Freq14);
        Assert.Equal(14_010_000UL, (ulong)(await call.WaitAsync(Limits.Test)).Hz);
    }

    [Fact]
    public async Task GetAsync_WithUIntKey()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<SecurityCode, uint>(0x12345678);
        Assert.Equal(Hex.Parse("08 20 0B 00 78 56 34 12"), await device.ReadRequestAsync());
        await device.SendAsync("08 00 0B 00 EF BE AD DE");
        Assert.Equal(0xDEADBEEFu, (await call.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task GetAsync_WithoutKey()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<TargetName>();
        Assert.Equal(Hex.Parse("04 20 01 00"), await device.ReadRequestAsync());
        await device.SendAsync("0B 00 01 00 53 44 52 2D 49 50 00");
        Assert.Equal("SDR-IP", (await call.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task GetRangeAsync_ExpectsRangeResponse()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetRangeAsync<FrequencyRanges, byte>(0);
        Assert.Equal(Hex.Parse("05 40 20 00 00"), await device.ReadRequestAsync());
        await device.SendAsync(RangesFrame);
        Assert.Equal(2, (await call.WaitAsync(Limits.Test)).Ranges.Length);
    }

    [Fact]
    public async Task SendAsync_Raw_ReturnsMessage()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.SendAsync(RequestType.Get, 0x0005, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(Hex.Parse("04 20 05 00"), await device.ReadRequestAsync());
        await device.SendAsync("05 00 05 00 0B");
        var message = await call.WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0x0005), (message.Type, message.Code));
        Assert.Equal(new byte[] { 0x0B }, message.Payload.ToArray());
    }

    [Theory]
    [InlineData(RequestType.DataAck)]
    [InlineData(RequestType.Data0)]
    public async Task SendAsync_NonControlType_Throws(RequestType type)
    {
        await using var device = PipeDevice.Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => device.Client.SendAsync(type, 1, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task Nak_FailsRequest()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("02 00");
        var ex = await Assert.ThrowsAsync<NetSdrNakException>(() => call.WaitAsync(Limits.Test));
        Assert.Equal(((ushort)0x0009, RequestType.Get), (ex.Code, ex.RequestType));
    }

    [Fact]
    public async Task ForeignCode_FailsRequest_GoesToUnsolicited_ConnectionAlive()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("06 00 03 00 11 02");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
        var message = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0x0003), (message.Type, message.Code));
        Assert.True(device.Client.IsConnected);
    }

    [Fact]
    public async Task WrongReplyType_FailsRequest()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("0A 40 20 00 00 90 C6 D5 00 00");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task ShortPayload_FailsRequest_ConnectionAlive()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("07 00 20 00 00 90 C6");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
        var next = device.Client.GetAsync<ReceiverFrequency, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(Freq14);
        Assert.Equal(14_010_000UL, (ulong)(await next.WaitAsync(Limits.Test)).Hz);
    }

    [Fact]
    public async Task UnsolicitedDuringRequest_IsNotTakenAsResponse()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("06 20 48 00 00 03");
        await device.SendAsync("06 00 48 00 00 0A");
        Assert.Equal(10, (await call.WaitAsync(Limits.Test)).Level);
        var message = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal(ReplyType.Unsolicited, message.Type);
        Assert.True(message.Is<AfGain>());
        Assert.Equal(3, message.As<AfGain>().Level);
    }

    [Fact]
    public async Task ResponseWithoutRequest_GoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("05 00 05 00 0B");
        var message = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Response, (ushort)0x0005), (message.Type, message.Code));
    }

    [Fact]
    public async Task DataAckAndDataItems_GoToUnsolicitedWithCodeZero()
    {
        await using var device = PipeDevice.Create();
        await device.SendAsync("03 60 02");
        await device.SendAsync("06 80 01 02 03 04");
        var ack = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var data = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.DataAck, (ushort)0), (ack.Type, ack.Code));
        Assert.Equal(new byte[] { 2 }, ack.Payload.ToArray());
        Assert.Equal((ReplyType.Data0, (ushort)0), (data.Type, data.Code));
        Assert.Equal(Hex.Parse("01 02 03 04"), data.Payload.ToArray());
    }

    [Fact]
    public async Task UnsolicitedOverflow_DropsOldest()
    {
        await using var device = PipeDevice.Create(new NetSdrControlClientOptions { UnsolicitedCapacity = 2 });
        await device.SendAsync("06 20 48 00 00 01");
        await device.SendAsync("06 20 48 00 00 02");
        await device.SendAsync("06 20 48 00 00 03");
        var sync = device.Client.GetAsync<ProductId>();          // reader handles frames in order,
        await device.ReadRequestAsync();                          // so after this reply all three
        await device.SendAsync("08 00 09 00 53 44 52 03");        // unsolicited frames were processed
        await sync.WaitAsync(Limits.Test);
        Assert.True(device.Client.Unsolicited.TryRead(out var first));
        Assert.True(device.Client.Unsolicited.TryRead(out var second));
        Assert.False(device.Client.Unsolicited.TryRead(out _));
        Assert.Equal((2, 3), (first.As<AfGain>().Level, second.As<AfGain>().Level));
    }

    [Fact]
    public async Task Frame_OneByteAtATime()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<TargetName>();
        await device.ReadRequestAsync();
        await device.SendBytewiseAsync("0B 00 01 00 53 44 52 2D 49 50 00");
        Assert.Equal("SDR-IP", (await call.WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task TwoFramesInOneWrite()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<AfGain, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync("06 20 48 00 00 03 06 00 48 00 00 0A");
        Assert.Equal(10, (await call.WaitAsync(Limits.Test)).Level);
        Assert.Equal(3, (await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).As<AfGain>().Level);
    }

    [Fact]
    public async Task Frame_AcrossPipeSegments()
    {
        await using var device = PipeDevice.Create(toClient: new PipeOptions(minimumSegmentSize: 16));
        var call = device.Client.GetRangeAsync<FrequencyRanges, byte>(0);
        await device.ReadRequestAsync();
        await device.SendAsync(RangesFrame);
        var ranges = await call.WaitAsync(Limits.Test);
        Assert.Equal(new FrequencyRange(140_000_000, 150_000_000, 160_000_000), ranges.Ranges[1]);
    }

    [Fact]
    public async Task BrokenHeader_FaultsClient()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.SendAsync("01 00");
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<NetSdrProtocolException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        Assert.False(device.Client.IsConnected);
    }

    [Fact]
    public async Task OversizedPayload_ThrowsBeforeWrite()
    {
        await using var device = PipeDevice.Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => device.Client.SendAsync(RequestType.Set, 0x0150, new byte[8188]));
        var call = device.Client.SetAsync(new RfGain(0, -20));
        Assert.Equal(Hex.Parse("06 00 38 00 00 EC"), await device.ReadRequestAsync());
        await device.SendAsync("06 00 38 00 00 EC");
        Assert.Equal(-20, (await call.WaitAsync(Limits.Test)).GainDb);
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ControlClientProtocolTests"`
Expected: збірка падає, клієнта немає.

- [ ] **Step 3: Реалізувати `ControlItemMessage`, `NetSdrControlClientOptions`, `PendingRequest*`, `NetSdrControlClient` (без TCP і таймаутів; ними займається Task 6)**

Цикл читання:

```csharp
while (true)
{
    var result = await _input.ReadAsync(_lifetime.Token);
    var buffer = result.Buffer;
    while (TryTakeFrame(ref buffer, out var frame, out var type, out var length))  // throws NetSdrProtocolException
        ProcessFrame(frame, type, length);                                         // on invalid header
    _input.AdvanceTo(buffer.Start, buffer.End);
    if (result.IsCompleted) throw new IOException("Connection closed by the device.");
}
```

`_pending` захищає один замок; `SemaphoreSlim(1, 1)` тримає лише один запит у польоті.

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ControlClientProtocolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: control client framing and reply matching" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Клієнт керування: таймаути, скасування, життєвий цикл, TCP

**Files:**
- Modify: `NetSdr/Control/NetSdrControlClient.cs`
- Test: `NetSdr.Tests/Control/ControlClientLifecycleTests.cs`

**Interfaces:**
- Consumes: `PipeDevice` і клієнт із Task 5.
- Produces: `ConnectAsync(string host, int port = 50000, CancellationToken ct = default)` через `new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }` і `ConnectAsync(host, port, ct)`; `ConnectAsync(IPEndPoint endPoint, CancellationToken ct = default)` через сокет з `endPoint.AddressFamily`. Обидва потім викликають `Attach(PipeReader.Create(stream), stream)` з `new NetworkStream(socket, ownsSocket: true)`. Повторне підключення кидає `InvalidOperationException`. TCP-шлях перевіряє Task 7 разом із сервером.

Правила:
- Очікування відповіді: `pending.Task.WaitAsync(options.ResponseTimeout, ct)`.
- Таймаут при `FaultOnTimeout = true`: виклик кидає `TimeoutException`, клієнт у стані збою з тим самим винятком (`Completion`, закриття потоків, `IsConnected = false`).
- Таймаут при `FaultOnTimeout = false` і скасування після запису кадру: виклик кидає `TimeoutException` або `OperationCanceledException`, з'єднання живе, пара `(Code, ExpectedType)` запиту стає покинутою (одна комірка, нова перезаписує стару). Відповідь із цією парою, що не збігається з активним запитом, іде в `Unsolicited`, комірка очищується, активний запит не чіпається. NAK завжди адресується активному запиту; без нього він очищує комірку і йде в `Unsolicited`. Скасування до запису кадру (у черзі семафора) лише кидає `OperationCanceledException`.
- Помилка запису в потік переводить клієнт у стан збою з `IOException`.
- `DisposeAsync` ідемпотентний: зупиняє цикл, закриває потоки, провалює активний запит `ObjectDisposedException`, завершує `Unsolicited`, `Completion` завершується успішно.
- Виклик після `DisposeAsync` кидає `ObjectDisposedException`, у стані збою `InvalidOperationException` (inner це причина збою), до підключення `InvalidOperationException`.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class ControlClientLifecycleTests
{
    static NetSdrControlClientOptions Fast(bool fault = true) =>
        new() { ResponseTimeout = TimeSpan.FromMilliseconds(150), FaultOnTimeout = fault };

    [Fact]
    public async Task Timeout_WithFaultOnTimeout_FaultsClient()
    {
        await using var device = PipeDevice.Create(Fast());
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<TimeoutException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        Assert.False(device.Client.IsConnected);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => device.Client.GetAsync<ProductId>());
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Timeout_WithoutFault_KeepsConnection()
    {
        await using var device = PipeDevice.Create(Fast(fault: false));
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(Limits.Test));
        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("06 00 03 00 11 02");
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
    }

    [Fact]
    public async Task Cancellation_ThrowsAndKeepsClient()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var call = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Limits.Test));
        Assert.True(device.Client.IsConnected);
    }

    [Fact]
    public async Task LateReplyOfAbandonedRequest_GoesToUnsolicited()
    {
        await using var device = PipeDevice.Create();
        using var cts = new CancellationTokenSource();
        var abandoned = device.Client.GetAsync<ProductId>(cts.Token);
        await device.ReadRequestAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Limits.Test));

        var next = device.Client.GetAsync<InterfaceVersion>();
        await device.ReadRequestAsync();
        await device.SendAsync("08 00 09 00 53 44 52 03");   // late reply to the cancelled request
        await device.SendAsync("06 00 03 00 11 02");
        Assert.Equal(529, (await next.WaitAsync(Limits.Test)).Version);
        var late = await device.Client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ushort)0x0009, late.Code);
    }

    [Fact]
    public async Task RemoteClose_FailsRequestAndCompletion()
    {
        await using var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        device.CloseRemote();
        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<IOException>(() => device.Client.Completion.WaitAsync(Limits.Test));
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
    }

    [Fact]
    public async Task Dispose_FailsPending_CompletesSuccessfully()
    {
        var device = PipeDevice.Create();
        var call = device.Client.GetAsync<ProductId>();
        await device.ReadRequestAsync();
        await device.Client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => call.WaitAsync(Limits.Test));
        await device.Client.Completion.WaitAsync(Limits.Test);
        await device.Client.Unsolicited.Completion.WaitAsync(Limits.Test);
        await device.Client.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.Client.GetAsync<ProductId>());
        await device.DisposeAsync();
    }

    [Fact]
    public async Task BeforeConnect_Throws()
    {
        await using var client = new NetSdrControlClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<ProductId>());
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ControlClientLifecycleTests"`
Expected: FAIL. Таймаути не реалізовано, тести таймауту і покинутого запиту падають.

- [ ] **Step 3: Реалізувати правила задачі в `NetSdrControlClient`**

- [ ] **Step 4: Запустити тести клієнта**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~ControlClient"`
Expected: PASS, зокрема всі тести Task 5.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: control client timeouts, cancellation and lifecycle" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Тестовий сервер: канал керування

**Files:**
- Create: `NetSdr.Testing/NetSdrTestServer.cs`, `NetSdr.Testing/ControlRequest.cs`, `NetSdr.Testing/ControlReply.cs`
- Test: `NetSdr.Tests/Testing/TestServerControlTests.cs`

**Interfaces:**
- Consumes: `NetSdrControlClient` (Tasks 5, 6), `ControlFrames`, `Eventually` (Task 2), структури.
- Produces: API специфікації 7.1 для керування: `NetSdrTestServer()` і `StartAsync`, `Port`, `ClientConnected`, `OnRequest<T>`, `OnRequest(ushort, ...)`, `Preload<T>`, `Received`, `SendUnsolicitedAsync<T>`, `SendUnsolicitedAsync(ushort, ReadOnlyMemory<byte>)`, `DisconnectClientAsync`, `DisposeAsync`; `ControlRequest` (`internal` конструктор), `ControlRequest<T>` з `Key<TKey>() => MemoryMarshal.Read<TKey>(Payload.Span)`; `ControlReply` (`Echo`, `Nak`, `Silent`, `Bytes`, `Item<T>`, `After`). Для Task 9 сервер зберігає `IPEndPoint` поточного клієнта і має приватний доступ до стану `bool TryGetLatestState(ushort code, out byte[] payload)`.

Правила:
- Слухає `IPAddress.Loopback:port`. Клієнти по одному: після відключення приймається наступний. `ClientConnected` завершується на першому підключенні.
- Кожен кадр керування від клієнта записується в `Received` (копія payload; властивість повертає знімок-масив) і обробляється за схемою 7.2 специфікації. Кадри даних від клієнта ігноруються.
- Стан: до 16 останніх payload на код. Set додає payload; Get повертає найновіший payload, що починається з ключа (порожній ключ дає найновіший), інакше NAK; GetRange без обробника дає NAK. `Preload<T>` додає `T.Write(item)`.
- Відповідь: `Echo` повертає код і payload запиту; `Bytes` дає код запиту і заданий payload; `Item<T>` дає `T.Code` і `T.Write(item)`. Тип відповіді `RangeResponse` для GetRange, інакше `Response`. `Nak` це `02 00`. `After(d)` спершу чекає `d`. Виняток у обробнику перетворюється на `Nak`.
- Запис у сокет під одним `SemaphoreSlim`, спільним для відповідей і `SendUnsolicitedAsync`. `SendUnsolicitedAsync` без клієнта кидає `InvalidOperationException`.
- `DisconnectClientAsync` закриває сокет поточного клієнта. `DisposeAsync` зупиняє listener і закриває клієнта.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class TestServerControlTests
{
    static async Task<(NetSdrTestServer Server, NetSdrControlClient Client)> StartAsync(
        Action<NetSdrTestServer>? setup = null, NetSdrControlClientOptions? options = null)
    {
        var server = new NetSdrTestServer();
        setup?.Invoke(server);
        await server.StartAsync();
        var client = new NetSdrControlClient(options);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        return (server, client);
    }

    [Fact]
    public async Task Set_EchoesAndGetReturnsState()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        Assert.Equal(-20, (await client.SetAsync(new RfGain(0, -20))).GainDb);
        Assert.Equal(-20, (await client.GetAsync<RfGain, byte>(0)).GainDb);
    }

    [Fact]
    public async Task Get_MatchesKeyPerChannel()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new ReceiverFrequency(0, 7_000_000));
        await client.SetAsync(new ReceiverFrequency(2, 14_000_000));
        Assert.Equal(7_000_000UL, (ulong)(await client.GetAsync<ReceiverFrequency, byte>(0)).Hz);
        Assert.Equal(14_000_000UL, (ulong)(await client.GetAsync<ReceiverFrequency, byte>(2)).Hz);
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ReceiverFrequency, byte>(1));
    }

    [Fact]
    public async Task UnknownGet_And_GetRange_Nak()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<ProductId>());
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetRangeAsync<FrequencyRanges, byte>(0));
    }

    [Fact]
    public async Task Preload_ServesGet_And_HandlerOverridesState()
    {
        var (server, client) = await StartAsync(s =>
        {
            s.Preload(new ProductId(0x03524453));
            s.Preload(new InterfaceVersion(529));
            s.OnRequest<InterfaceVersion>(_ => ControlReply.Item(new InterfaceVersion(900)));
        });
        await using var _ = server; await using var __ = client;
        Assert.Equal(0x03524453u, (await client.GetAsync<ProductId>()).Value);
        Assert.Equal(900, (await client.GetAsync<InterfaceVersion>()).Version);
    }

    [Fact]
    public async Task TypedHandler_SeesItemForSet_AndKeyForGet()
    {
        var seen = new ConcurrentQueue<ControlRequest<ReceiverFrequency>>();
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverFrequency>(r =>
        {
            seen.Enqueue(r);
            return r.Type == RequestType.Set
                ? ControlReply.Echo
                : ControlReply.Item(new ReceiverFrequency(r.Key<byte>(), 123));
        }));
        await using var _ = server; await using var __ = client;
        await client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        var got = await client.GetAsync<ReceiverFrequency, byte>(2);
        Assert.Equal((2, 123UL), (got.Channel, (ulong)got.Hz));
        var requests = seen.ToArray();
        Assert.Equal(14_010_000UL, (ulong)requests[0].Item.Hz);
        Assert.Equal(default, requests[1].Item);
    }

    [Fact]
    public async Task RawHandler_Bytes()
    {
        var (server, client) = await StartAsync(s => s.OnRequest(0x0150, _ => ControlReply.Bytes(Hex.Parse("01 2A 00 00 00"))));
        await using var _ = server; await using var __ = client;
        var item = await client.GetAsync<MyVendorItem>();
        Assert.Equal((1, 42u), (item.Channel, item.Value));
    }

    [Fact]
    public async Task Received_RecordsRequestsInOrder()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<NetSdrNakException>(() => client.GetAsync<TargetName>());
        await client.SetAsync(new RfGain(0, -10));
        var received = server.Received;
        Assert.Equal((RequestType.Get, (ushort)0x0001, 0), (received[0].Type, received[0].Code, received[0].Payload.Length));
        Assert.Equal((RequestType.Set, (ushort)0x0038), (received[1].Type, received[1].Code));
        Assert.Equal(Hex.Parse("00 F6"), received[1].Payload.ToArray());
    }

    [Fact]
    public async Task After_DelaysReply()
    {
        var (server, client) = await StartAsync(s =>
            s.OnRequest<ProductId>(_ => ControlReply.Item(new ProductId(1)).After(TimeSpan.FromMilliseconds(300))));
        await using var _ = server; await using var __ = client;
        var watch = Stopwatch.StartNew();
        await client.GetAsync<ProductId>();
        Assert.True(watch.ElapsedMilliseconds >= 250);
    }

    [Fact]
    public async Task Silent_LeadsToTimeoutFault()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ProductId>(_ => ControlReply.Silent),
            new NetSdrControlClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync<ProductId>());
        await Assert.ThrowsAsync<TimeoutException>(() => client.Completion.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task SendUnsolicited_ArrivesAtClient()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await server.ClientConnected.WaitAsync(Limits.Test);
        await server.SendUnsolicitedAsync(new AfGain(0, 3));
        await server.SendUnsolicitedAsync(0x0005, Hex.Parse("20"));
        var gain = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        var status = await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test);
        Assert.Equal((ReplyType.Unsolicited, 3), (gain.Type, gain.As<AfGain>().Level));
        Assert.Equal(new[] { StatusCodes.AdOverload }, status.As<StatusCodes>().Codes);
    }

    [Fact]
    public async Task UnsolicitedFromHandler_BeforeReply_DoesNotBreakRequest()
    {
        NetSdrTestServer? self = null;
        var (server, client) = await StartAsync(s =>
        {
            self = s;
            s.OnRequest<AfGain>(r =>
            {
                self!.SendUnsolicitedAsync(0x0005, Hex.Parse("20")).GetAwaiter().GetResult();
                return ControlReply.Item(new AfGain(r.Key<byte>(), 7));
            });
        });
        await using var _ = server; await using var __ = client;
        Assert.Equal(7, (await client.GetAsync<AfGain, byte>(0)).Level);
        Assert.Equal((ushort)0x0005, (await client.Unsolicited.ReadAsync().AsTask().WaitAsync(Limits.Test)).Code);
    }

    [Fact]
    public async Task DisconnectClient_FailsActiveRequest()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ProductId>(_ => ControlReply.Silent));
        await using var _ = server; await using var __ = client;
        var call = client.GetAsync<ProductId>();
        await Eventually.ThatAsync(() => server.Received.Count == 1);
        await server.DisconnectClientAsync();
        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Limits.Test));
        await Assert.ThrowsAsync<IOException>(() => client.Completion.WaitAsync(Limits.Test));
    }

    [Fact]
    public async Task SecondClient_ConnectsAfterFirstLeaves()
    {
        var (server, first) = await StartAsync(s => s.Preload(new ProductId(5)));
        await using var _ = server;
        await first.DisposeAsync();
        await using var second = new NetSdrControlClient();
        await second.ConnectAsync("127.0.0.1", server.Port);
        Assert.Equal(5u, (await second.GetAsync<ProductId>()).Value);
    }

    [Fact]
    public async Task ConnectTwice_Throws()
    {
        var (server, client) = await StartAsync();
        await using var _ = server; await using var __ = client;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port)));
    }

    [Fact]
    public async Task TenConcurrentCalls_AllGetTheirOwnReplies()
    {
        var (server, client) = await StartAsync(s => s.OnRequest<ReceiverFrequency>(r =>
            ControlReply.Item(new ReceiverFrequency(r.Key<byte>(), 1000UL + r.Key<byte>()))
                .After(TimeSpan.FromMilliseconds(5))));
        await using var _ = server; await using var __ = client;
        var calls = Enumerable.Range(0, 10).Select(ch => client.GetAsync<ReceiverFrequency, byte>((byte)ch)).ToArray();
        var results = await Task.WhenAll(calls).WaitAsync(Limits.Test);
        for (var ch = 0; ch < 10; ch++)
            Assert.Equal((ch, 1000UL + (ulong)ch), (results[ch].Channel, (ulong)results[ch].Hz));
        Assert.Equal(10, server.Received.Count);
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~TestServerControlTests"`
Expected: збірка падає, сервера немає.

- [ ] **Step 3: Реалізувати `ControlRequest`, `ControlReply`, `NetSdrTestServer` (керування) за правилами задачі**

- [ ] **Step 4: Запустити всі тести**

Run: `dotnet test NetSdr.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: test server control channel" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Приймач даних

**Files:**
- Create: `NetSdr/Data/NetSdrDataReceiver.cs`, `DataReceiverOptions.cs`, `DataPacketInfo.cs` (разом із делегатом `DataPacketHandler`), `SampleFormat.cs`, `DataRate.cs`, `DataReceiverStatistics.cs`, `DataSequence.cs`
- Create: `NetSdr.Tests/Data/UdpTestSender.cs`, `NetSdr.Tests/Data/PacketCollector.cs`
- Test: `NetSdr.Tests/Data/DataSequenceTests.cs`, `DataRateTests.cs`, `DataReceiverTests.cs`

**Interfaces:**
- Consumes: `FrameHeader` (Task 1), `Eventually` (Task 2).
- Produces: публічне API специфікації 6.1 без змін, а також:
  - `static class DataSequence { static ushort Next(ushort sequence); static int Distance(ushort expected, ushort actual); }`. `Next(0xFFFF) == 1`. `Distance` дорівнює `actual >= expected ? actual - expected : actual + 0xFFFF - expected`.
  - Конструктор `DataPacketInfo` `internal`.
  - Тести: `UdpTestSender.Send(IPEndPoint target, byte[] datagram)` шле з `127.0.0.1`; `UdpTestSender.Datagram(ushort sequence, int totalLength, byte type = 4, int? headerLength = null)` пише заголовок типу `type` з довжиною `headerLength ?? totalLength` (довжина понад 8191 записується як 0), номер і payload, де байт `i` дорівнює `(byte)i`. `PacketCollector : IDisposable` з конструктором `(DataReceiverOptions? options = null, Action<NetSdrDataReceiver>? onPacket = null)`, `NetSdrDataReceiver Receiver`, `ConcurrentQueue<(DataPacketInfo Info, byte[] Samples)> Packets`, `IPEndPoint EndPoint`. Колектор прив'язує приймач до `new IPEndPoint(IPAddress.Loopback, 0)` і запускає його.

Правила:
- Сокет IPv4 UDP створюється в конструкторі з `ReceiveBufferSize = InitialReceiveBufferBytes`. `Bind(int)` прив'язує до `IPAddress.Any`. `Bind(IPEndPoint)` приймає лише IPv4, інакше `ArgumentException`. Повторний `Bind`, `Start` до `Bind`, повторний `Start` і `LocalEndPoint` до `Bind` кидають `InvalidOperationException`.
- Цикл за схемою 6.2 специфікації: фоновий `Thread` (`IsBackground`, `Priority = options.ThreadPriority`), буфер 65535 байтів, `ReceiveFrom(Span<byte>, SocketFlags.None, SocketAddress)` з одним `SocketAddress`. `RemoteAddress` порівнюється з байтами 4..7 у `SocketAddress.Buffer`, без алокацій на пакет.
- Пакет відкидається (`Rejected++`), якщо `n < 4`, тип не `Data0`, недійсний заголовок або `ValidateLength` і довжина із заголовка не дорівнює `n`.
- Послідовність: перший пакет або `seq == 0` дає `GapBefore = 0`. Інакше `d = Distance(expected, seq)`: якщо `d >= 0x8000`, пакет переставлений, доставляється з `GapBefore = 0`, `expected` і `Lost` не змінюються; інакше `GapBefore = d`, `Lost += d`, `expected = Next(seq)`. Для першого пакета і для нуля `expected = Next(seq)`.
- Статистика: `Received` рахує доставлені пакети, `Bytes` рахує байти семплів (`n - 4`); оновлюється через `Interlocked`.
- `Dispose` закриває сокет і чекає на потік, крім випадку, коли його викликали з потоку прийому. `SocketException` з `ConnectionReset` до `Dispose` пропускається, інші завершують цикл.
- `SetReceiveBuffer(TimeSpan d, long bps)` дорівнює `SetReceiveBuffer((int)Math.Min(int.MaxValue, Math.Ceiling(d.TotalSeconds * bps)))`.
- `DataRate.BytesPerSecond` дорівнює `(long)Math.Ceiling(sampleRate * (Int16 ? 4 : 6) * channels)`; `Unknown` кидає `ArgumentException`.

- [ ] **Step 1: Написати помічники і тести, що падають**

```csharp
public class DataSequenceTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(0xFFFE, 0xFFFF)] [InlineData(0xFFFF, 1)]
    public void Next(int value, int expected) => Assert.Equal(expected, DataSequence.Next((ushort)value));

    [Theory]
    [InlineData(5, 5, 0)] [InlineData(5, 7, 2)] [InlineData(0xFFFF, 1, 1)] [InlineData(0xFFFE, 2, 3)] [InlineData(3, 1, 65533)]
    public void Distance(int expected, int actual, int distance) =>
        Assert.Equal(distance, DataSequence.Distance((ushort)expected, (ushort)actual));
}

public class DataRateTests
{
    [Theory]
    [InlineData(500_000, SampleFormat.Int16, 1, 2_000_000)]
    [InlineData(1_333_333, SampleFormat.Int24, 1, 7_999_998)]
    [InlineData(200_000, SampleFormat.Int16, 2, 1_600_000)]
    public void BytesPerSecond(double rate, SampleFormat format, int channels, long expected) =>
        Assert.Equal(expected, DataRate.BytesPerSecond(rate, format, channels));

    [Fact]
    public void Unknown_Throws() =>
        Assert.Throws<ArgumentException>(() => DataRate.BytesPerSecond(1, SampleFormat.Unknown));
}

public class DataReceiverTests
{
    static void Send(PacketCollector c, params byte[][] datagrams)
    {
        foreach (var d in datagrams) UdpTestSender.Send(c.EndPoint, d);
    }

    static DataPacketInfo[] Infos(PacketCollector c) => c.Packets.Select(p => p.Info).ToArray();

    [Fact]
    public void Bind_Port0_AssignsPort()
    {
        using var c = new PacketCollector();
        Assert.Equal(IPAddress.Loopback, c.EndPoint.Address);
        Assert.NotEqual(0, c.EndPoint.Port);
    }

    [Theory]
    [InlineData(1028, SampleFormat.Int16)] [InlineData(516, SampleFormat.Int16)]
    [InlineData(1444, SampleFormat.Int24)] [InlineData(388, SampleFormat.Int24)]
    [InlineData(104, SampleFormat.Unknown)]
    public async Task Format_FromDatagramLength(int length, SampleFormat format)
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, length));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        var (info, samples) = c.Packets.Single();
        Assert.Equal(format, info.Format);
        Assert.Equal(length - 4, samples.Length);
        Assert.Equal(UdpTestSender.Datagram(0, length)[4..], samples);
    }

    [Fact]
    public async Task Gap_CountsLostAndGapBefore()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(4, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 3);
        Assert.Equal(new[] { 0, 0, 2 }, Infos(c).Select(i => i.GapBefore));
        Assert.Equal(new[] { true, false, false }, Infos(c).Select(i => i.IsCaptureStart));
        Assert.Equal(2, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Wrap_FFFF_To_1_IsNotAGap()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0xFFFE, 1028), UdpTestSender.Datagram(0xFFFF, 1028),
            UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 4);
        Assert.All(Infos(c), i => Assert.Equal(0, i.GapBefore));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task ZeroMidStream_StartsNewCapture()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(5, 1028), UdpTestSender.Datagram(6, 1028),
            UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 4);
        Assert.Equal(new[] { false, false, true, false }, Infos(c).Select(i => i.IsCaptureStart));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Reordered_IsNotCountedAsLost()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(2, 1028),
            UdpTestSender.Datagram(1, 1028), UdpTestSender.Datagram(3, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 5);
        Assert.All(Infos(c), i => Assert.Equal(0, i.GapBefore));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Rejects_ForeignTypeShortAndWrongLength()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028, type: 5), [0x04, 0x80, 0x00],
            UdpTestSender.Datagram(0, 1000, headerLength: 1028), UdpTestSender.Datagram(0, 1028));
        await Eventually.ThatAsync(() => c.Packets.Count == 1);
        Assert.Equal(3, c.Receiver.Statistics.Rejected);
    }

    [Fact]
    public async Task Jumbo9000_DeliveredOnlyWithoutValidation()
    {
        using var strict = new PacketCollector();
        using var loose = new PacketCollector(new DataReceiverOptions { ValidateLength = false });
        Send(strict, UdpTestSender.Datagram(0, 9000));
        Send(loose, UdpTestSender.Datagram(0, 9000));
        await Eventually.ThatAsync(() => loose.Packets.Count == 1 && strict.Receiver.Statistics.Rejected == 1);
        Assert.Equal(8996, loose.Packets.Single().Samples.Length);
        Assert.Equal(SampleFormat.Unknown, loose.Packets.Single().Info.Format);
    }

    [Fact]
    public async Task RemoteAddress_Filters()
    {
        using var other = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Parse("127.0.0.2") });
        using var same = new PacketCollector(new DataReceiverOptions { RemoteAddress = IPAddress.Loopback });
        Send(other, UdpTestSender.Datagram(0, 1028));
        Send(same, UdpTestSender.Datagram(0, 1028));
        await Eventually.ThatAsync(() => same.Packets.Count == 1 && other.Receiver.Statistics.Rejected == 1);
        Assert.Empty(other.Packets);
    }

    [Fact]
    public async Task HandlerException_IsCountedAndReceptionContinues()
    {
        var calls = 0;
        using var c = new PacketCollector(onPacket: _ => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException(); });
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(1, c.Receiver.Statistics.HandlerErrors);
    }

    [Fact]
    public async Task Statistics_CountReceivedAndSampleBytes()
    {
        using var c = new PacketCollector();
        Send(c, UdpTestSender.Datagram(0, 1028), UdpTestSender.Datagram(1, 1028));
        await Eventually.ThatAsync(() => c.Receiver.Statistics.Received == 2);
        Assert.Equal(2048, c.Receiver.Statistics.Bytes);
    }

    [Fact]
    public void SetReceiveBuffer_FromDuration()
    {
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        receiver.SetReceiveBuffer(TimeSpan.FromMilliseconds(200), 1_000_000);
        Assert.True(receiver.ActualReceiveBufferSize >= 200_000);
    }

    [Fact]
    public async Task Dispose_FromHandler_DoesNotDeadlock()
    {
        var disposed = new TaskCompletionSource();
        using var c = new PacketCollector(onPacket: r => { r.Dispose(); disposed.TrySetResult(); });
        Send(c, UdpTestSender.Datagram(0, 1028));
        await disposed.Task.WaitAsync(Limits.Test);
    }

    [Fact]
    public void StartRules()
    {
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo _, ReadOnlySpan<byte> _) => { });
        Assert.Throws<InvalidOperationException>(() => receiver.Start());
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();
        Assert.Throws<InvalidOperationException>(() => receiver.Start());
    }
}
```

`onPacket` колектора викликається з callback після запису пакета в `Packets`.

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~NetSdr.Tests.Data"`
Expected: збірка падає.

- [ ] **Step 3: Реалізувати типи `NetSdr.Data` за правилами задачі**

- [ ] **Step 4: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~NetSdr.Tests.Data"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: UDP data receiver" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Тестовий сервер: потік даних і наскрізний сценарій

**Files:**
- Create: `NetSdr.Testing/NetSdrTestServer.Streaming.cs`, `NetSdr.Testing/StreamOptions.cs`, `NetSdr.Testing/SampleSources.cs`
- Modify: `NetSdr.Testing/NetSdrTestServer.cs` (обробка Set 0x0018 при `AutoStream`, зупинка потоку в `DisposeAsync`)
- Test: `NetSdr.Tests/Testing/TestServerStreamingTests.cs`, `NetSdr.Tests/Testing/SampleSourcesTests.cs`, `NetSdr.Tests/EndToEndTests.cs`

**Interfaces:**
- Consumes: `NetSdrTestServer` (Task 7), `DataSequence`, `SampleFormat`, `PacketCollector`, `NetSdrDataReceiver` (Task 8).
- Produces:
  - `delegate void FillSamples(Span<byte> destination, long firstSampleIndex, SampleFormat format)`; `enum Pacing { RealTime, Unthrottled }`.
  - `sealed class StreamOptions { SampleFormat? Format; int? PayloadSize; double? SampleRate; int Channels = 1; Pacing Pacing = Pacing.RealTime; FillSamples Source = SampleSources.Counter(); Func<ushort, bool>? DropPacket; }`.
  - `static class SampleSources { FillSamples FromBuffer(ReadOnlyMemory<byte> samples); FillSamples Tone(double frequencyHz, double sampleRate, double amplitude); FillSamples Counter(); }`.
  - На `NetSdrTestServer`: `StreamOptions Stream { get; }`, `bool AutoStream { get; set; } = true`, `Task StartStreamingAsync(IPEndPoint target, StreamOptions? options = null)`, `Task StopStreamingAsync()`.

Правила:
- Компонента займає `c = Int16 ? 2 : 3` байти, комплексний семпл `2c`. Семплів у пакеті `PayloadSize / (2c)`, залишок payload заповнюється нулями.
- `Counter`: семпл `k` має `I = idx` і `Q = ~idx`, де `idx = firstSampleIndex + k`; кожну компоненту записують молодшими `c` байтами little-endian.
- `Tone`: `I = round(A·F·cos φ)`, `Q = round(A·F·sin φ)`, де `φ = 2π·f·idx/fs`, `F = 32767` для Int16 і `8388607` для Int24.
- `FromBuffer`: циклічне копіювання з позиції `(firstSampleIndex · 2c) mod length`.
- Пакет: заголовок типу `Data0` із довжиною `PayloadSize + 4`; довжина понад 8191 записується як 0 (для 8194 це штатне кодування, для довших jumbo-пакетів домовленість зі специфікації 7.3), тому сервер пише такий заголовок сам, а не через `FrameHeader.Write`. Далі номер і семпли. Номери: 0 на старті, потім `DataSequence.Next`. `DropPacket(seq) == true` пропускає надсилання, але номер і індекс семплів просуваються.
- `RealTime`: пакет `n` іде не раніше `start + n·T`, де `T = (PayloadSize / (2c·Channels)) / SampleRate` секунд (рахується за `Stopwatch`; паузи через `Task.Delay`, якщо до строку більше 1 мс; пачками можна). `Unthrottled` шле без пауз.
- `StartStreamingAsync(target, options)`: `options ?? Stream`; стан пристрою ігнорується; `Format ?? Int16`, `SampleRate ?? 200_000`, `PayloadSize ?? велике значення за замовчуванням для формату`. Повторний старт зупиняє попередній потік. `StopStreamingAsync` скасовує цикл і чекає на нього; повторний виклик нічого не робить.
- `AutoStream` (без обробника 0x0018): Set 0x0018 зберігається в стані. Run (`payload[1] == 0x02`) спершу відповідає Echo, потім стартує потік; Stop (`0x01`) спершу чекає зупинки потоку, потім відповідає Echo. Параметри беруться зі `Stream`, а `null`-поля зі стану. Формат з біта 7 `payload[2]`. Розмір за замовчуванням із 0x00C4 (`1` малий: 512 для Int16 і 384 для Int24; інакше 1024 і 1440). Частота з 0x00B8 (байти 1..4), інакше 200 000. Ціль з 0x00C5: `Ip == 0` дає IP TCP-клієнта з портом із 0x00C5; без 0x00C5 ціль це IP і порт TCP-клієнта.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class SampleSourcesTests
{
    [Fact]
    public void Counter_Int16_And_Int24()
    {
        var b16 = new byte[8];
        SampleSources.Counter()(b16, 5, SampleFormat.Int16);
        Assert.Equal(Hex.Parse("05 00 FA FF 06 00 F9 FF"), b16);
        var b24 = new byte[6];
        SampleSources.Counter()(b24, 1, SampleFormat.Int24);
        Assert.Equal(Hex.Parse("01 00 00 FE FF FF"), b24);
    }

    [Fact]
    public void FromBuffer_Loops()
    {
        var source = SampleSources.FromBuffer(Hex.Parse("01 02 03 04 05 06 07 08"));
        var b = new byte[12];
        source(b, 1, SampleFormat.Int16);
        Assert.Equal(Hex.Parse("05 06 07 08 01 02 03 04 05 06 07 08"), b);
    }

    [Fact]
    public void Tone_QuarterPeriod()
    {
        var b = new byte[12];
        SampleSources.Tone(1000, 8000, 0.5)(b, 0, SampleFormat.Int16);
        var s = MemoryMarshal.Cast<byte, short>(b);
        Assert.Equal(16384, s[0]);
        Assert.Equal(0, s[1]);
        Assert.InRange(s[4], -1, 1);
        Assert.Equal(16384, s[5]);
    }
}

public class TestServerStreamingTests
{
    static async Task<(NetSdrTestServer, NetSdrControlClient)> ConnectAsync(Action<NetSdrTestServer>? setup = null)
    {
        var server = new NetSdrTestServer();
        setup?.Invoke(server);
        await server.StartAsync();
        var client = new NetSdrControlClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        return (server, client);
    }

    static short[] Shorts(byte[] samples) => MemoryMarshal.Cast<byte, short>(samples).ToArray();

    [Fact]
    public async Task ManualStream_Int16_CounterData()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 256_000 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        var packets = c.Packets.ToArray();
        Assert.Equal((SampleFormat.Int16, (ushort)0, true), (packets[0].Info.Format, packets[0].Info.Sequence, packets[0].Info.IsCaptureStart));
        Assert.Equal(1024, packets[0].Samples.Length);
        var first = Shorts(packets[0].Samples);
        Assert.Equal((0, -1, 255, -256), (first[0], first[1], first[510], first[511]));
        Assert.Equal(256, Shorts(packets[1].Samples)[0]);
    }

    [Fact]
    public async Task ManualStream_Int24_CounterData()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { Format = SampleFormat.Int24, SampleRate = 240_000 });
        await Eventually.ThatAsync(() => c.Packets.Count >= 2);
        await server.StopStreamingAsync();
        var second = c.Packets.ElementAt(1);
        Assert.Equal((SampleFormat.Int24, 1440), (second.Info.Format, second.Samples.Length));
        Assert.Equal(Hex.Parse("F0 00 00 0F FF FF"), second.Samples[..6]);   // sample 240
    }

    [Fact]
    public async Task DropPacket_ProducesGap()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint,
            new StreamOptions { SampleRate = 256_000, DropPacket = seq => seq is 3 or 4 });
        await Eventually.ThatAsync(() => c.Packets.Any(p => p.Info.Sequence == 5));
        await server.StopStreamingAsync();
        var five = c.Packets.Single(p => p.Info.Sequence == 5);
        Assert.Equal(2, five.Info.GapBefore);
        Assert.Equal(5 * 256, Shorts(five.Samples)[0]);
        Assert.Equal(2, c.Receiver.Statistics.Lost);
    }

    [Fact]
    public async Task Jumbo_DeliveredWithoutValidation()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var strict = new PacketCollector();
        using var loose = new PacketCollector(new DataReceiverOptions { ValidateLength = false });
        var options = new StreamOptions { PayloadSize = 8996, SampleRate = 22_490 };
        await server.StartStreamingAsync(loose.EndPoint, options);
        await Eventually.ThatAsync(() => loose.Packets.Count >= 1);
        await server.StartStreamingAsync(strict.EndPoint, options);
        await Eventually.ThatAsync(() => strict.Receiver.Statistics.Rejected >= 1);
        await server.StopStreamingAsync();
        Assert.Equal(8996, loose.Packets.First().Samples.Length);
        Assert.Empty(strict.Packets);
    }

    [Theory]
    [InlineData(false, DataOutputPacketSize.Large, 1028)]
    [InlineData(false, DataOutputPacketSize.Small, 516)]
    [InlineData(true, DataOutputPacketSize.Large, 1444)]
    [InlineData(true, DataOutputPacketSize.Small, 388)]
    public async Task AutoStream_PacketSizeFromState(bool bits24, byte size, int datagramLength)
    {
        var (server, client) = await ConnectAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new DataOutputPacketSize(size));
        await client.SetAsync(new OutputSampleRate(0, 100_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        Assert.Equal(datagramLength - 4, c.Packets.First().Samples.Length);
        Assert.Equal(bits24 ? SampleFormat.Int24 : SampleFormat.Int16, c.Packets.First().Info.Format);
    }

    [Fact]
    public async Task AutoStream_StopEndsStream()
    {
        var (server, client) = await ConnectAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 3);
        await client.SetAsync(ReceiverState.Stop);
        await Task.Delay(100);
        var count = c.Packets.Count;
        await Task.Delay(300);
        Assert.Equal(count, c.Packets.Count);
    }

    [Fact]
    public async Task AutoStream_ZeroIp_UsesClientAddress()
    {
        var (server, client) = await ConnectAsync();
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(new DataOutputUdpAddress(0, (ushort)c.EndPoint.Port));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
    }

    [Fact]
    public async Task AutoStream_ExplicitOptionsWin()
    {
        var (server, client) = await ConnectAsync(s => s.Stream.PayloadSize = 200);
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Eventually.ThatAsync(() => c.Packets.Count >= 1);
        Assert.Equal(200, c.Packets.First().Samples.Length);
    }

    [Fact]
    public async Task AutoStream_Disabled_NoData()
    {
        var (server, client) = await ConnectAsync(s => s.AutoStream = false);
        await using var _ = server; await using var __ = client;
        using var c = new PacketCollector();
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: false));
        await Task.Delay(300);
        Assert.Empty(c.Packets);
    }

    [Fact]
    public async Task RealTimePacing_LimitsRate()
    {
        await using var server = new NetSdrTestServer();
        await server.StartAsync();
        using var c = new PacketCollector();
        await server.StartStreamingAsync(c.EndPoint, new StreamOptions { SampleRate = 2_560 });   // 10 packets/s
        await Task.Delay(500);
        await server.StopStreamingAsync();
        Assert.InRange(c.Packets.Count, 2, 10);
    }
}

public class EndToEndTests
{
    [Fact]
    public async Task TypicalScenario_FromSpec()
    {
        await using var server = new NetSdrTestServer();
        server.OnRequest(TargetName.Code, _ => ControlReply.Bytes("SDR-IP\0"u8.ToArray()));
        await server.StartAsync();
        await using var client = new NetSdrControlClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port));
        Assert.Equal("SDR-IP", (await client.GetAsync<TargetName>()).Value);

        using var c = new PacketCollector();
        c.Receiver.SetReceiveBuffer(TimeSpan.FromMilliseconds(200), DataRate.BytesPerSecond(500_000, SampleFormat.Int24));
        await client.SetAsync(new OutputSampleRate(0, 500_000));
        await client.SetAsync(new ReceiverFrequency(0, 14_010_000));
        await client.SetAsync(DataOutputUdpAddress.For(c.EndPoint));
        await client.SetAsync(ReceiverState.Start(complex: true, bits24: true));
        await Eventually.ThatAsync(() => c.Packets.Count >= 10);
        await client.SetAsync(ReceiverState.Stop);

        var infos = c.Packets.Take(10).Select(p => p.Info).ToArray();
        Assert.All(infos, i => Assert.Equal(SampleFormat.Int24, i.Format));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (ushort)i), infos.Select(i => i.Sequence));
        Assert.Equal(0, c.Receiver.Statistics.Lost);
    }
}
```

`PacketCollector` прив'язується до loopback до `SetReceiveBuffer`; виклик після `Start` для сокета допустимий.

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~TestServerStreamingTests|FullyQualifiedName~SampleSourcesTests|FullyQualifiedName~EndToEndTests"`
Expected: збірка падає.

- [ ] **Step 3: Реалізувати `StreamOptions`, `SampleSources`, потік і `AutoStream` за правилами задачі**

- [ ] **Step 4: Запустити всі тести тричі поспіль, щоб відловити нестабільні**

Run: `dotnet test NetSdr.sln` (тричі)
Expected: PASS щоразу.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: test server data streaming" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Vega: проєкти і пункти протоколу

**Files:**
- Create: `examples/Vega/NetSdr.Examples.Vega/NetSdr.Examples.Vega.csproj`, `VegaProtocol.cs`, `Items/AntennaPort.cs`, `Items/TemperatureSensor.cs`, `Items/OverloadFlags.cs`, `Items/VendorUnlock.cs`, `Items/AntennaSelect.cs`, `Items/BoardTemperature.cs`, `Items/DeviceLabel.cs`, `Items/OverloadEvent.cs`
- Create: `examples/Vega/NetSdr.Examples.Vega.Tests/NetSdr.Examples.Vega.Tests.csproj`, `Hex.cs`, `Limits.cs`
- Test: `examples/Vega/NetSdr.Examples.Vega.Tests/Items/VegaItemTests.cs`

**Interfaces:**
- Consumes: `IControlItem<T>`, `ControlFrames`.
- Produces (простір імен `NetSdr.Examples.Vega` і `NetSdr.Examples.Vega.Items`):
  - `static class VegaProtocol { const uint ProductId = 0x41474556; const int MaxLabelLength = 32; const ushort VendorUnlockCode = 0x8000, AntennaSelectCode = 0x8001, BoardTemperatureCode = 0x8002, DeviceLabelCode = 0x8003, OverloadEventCode = 0x8004; }`
  - `enum AntennaPort : byte { A = 0, B = 1, Loop = 2 }`, `enum TemperatureSensor : byte { Board = 0, Adc = 1, Fpga = 2 }`, `[Flags] enum OverloadFlags : byte { None = 0, Adc = 1, Rf = 2 }`
  - `VendorUnlock(uint key)` з полем `uint Key`; `AntennaSelect(byte channel, AntennaPort port)` з полями `Channel`, `Port`; `BoardTemperature(TemperatureSensor sensor, short centiCelsius)` з полями `Sensor`, `CentiCelsius`, властивістю `double Celsius => CentiCelsius / 100.0` і `static BoardTemperature FromCelsius(TemperatureSensor sensor, double celsius)` (`(short)Math.Round(celsius * 100)`); `OverloadEvent(byte channel, OverloadFlags flags)` з полями `Channel`, `Flags`. Усі blittable, Pack = 1, коди з `VegaProtocol`.
  - `readonly struct DeviceLabel : IControlItem<DeviceLabel>`: `string Value` (`default` дає `""`), конструктор `(string value)` кидає `ArgumentException` для довжини понад `MaxLabelLength` або символу понад 0x7F; `GetSize` це `Value.Length + 1`; `Write` пише ASCII і нуль; `Read` читає до першого нуля або до кінця.

- [ ] **Step 1: Створити проєкти**

```bash
dotnet new classlib -n NetSdr.Examples.Vega -o examples/Vega/NetSdr.Examples.Vega
dotnet new xunit -n NetSdr.Examples.Vega.Tests -o examples/Vega/NetSdr.Examples.Vega.Tests
rm examples/Vega/NetSdr.Examples.Vega/Class1.cs examples/Vega/NetSdr.Examples.Vega.Tests/UnitTest1.cs
dotnet sln NetSdr.sln add --solution-folder examples examples/Vega/NetSdr.Examples.Vega/NetSdr.Examples.Vega.csproj examples/Vega/NetSdr.Examples.Vega.Tests/NetSdr.Examples.Vega.Tests.csproj
dotnet add examples/Vega/NetSdr.Examples.Vega/NetSdr.Examples.Vega.csproj reference NetSdr/NetSdr.csproj
dotnet add examples/Vega/NetSdr.Examples.Vega.Tests/NetSdr.Examples.Vega.Tests.csproj reference examples/Vega/NetSdr.Examples.Vega/NetSdr.Examples.Vega.csproj NetSdr.Testing/NetSdr.Testing.csproj
```

`Hex.cs` і `Limits.cs` у тестах Vega такі самі, як у `NetSdr.Tests`. Тести прикладу не посилаються на `NetSdr.Tests`.

- [ ] **Step 2: Написати тести, що падають**

```csharp
public class VegaItemTests
{
    [Fact] public void VendorUnlock_Frame() =>
        Assert.Equal(Hex.Parse("08 00 00 80 C5 5E DE C0"), ControlFrames.Request(RequestType.Set, new VendorUnlock(0xC0DE_5EC5)));

    [Fact]
    public void AntennaSelect_SetAndGetFrames()
    {
        Assert.Equal(Hex.Parse("06 00 01 80 00 01"), ControlFrames.Request(RequestType.Set, new AntennaSelect(0, AntennaPort.B)));
        Assert.Equal(Hex.Parse("05 20 01 80 01"), ControlFrames.Encode((byte)RequestType.Get, AntennaSelect.Code, [1]));
    }

    [Fact]
    public void BoardTemperature_NegativeUnsolicited()
    {
        var item = BoardTemperature.FromCelsius(TemperatureSensor.Adc, -12.5);
        var frame = ControlFrames.Reply(ReplyType.Unsolicited, item);
        Assert.Equal(Hex.Parse("07 20 02 80 01 1E FB"), frame);
        Assert.Equal(-12.5, ControlFrames.Decode<BoardTemperature>(frame).Celsius);
    }

    [Fact] public void OverloadEvent_Frame() =>
        Assert.Equal(Hex.Parse("06 20 04 80 01 03"),
            ControlFrames.Reply(ReplyType.Unsolicited, new OverloadEvent(1, OverloadFlags.Adc | OverloadFlags.Rf)));

    [Fact]
    public void DeviceLabel_RoundTrip()
    {
        var frame = ControlFrames.Request(RequestType.Set, new DeviceLabel("VEGA-1"));
        Assert.Equal(Hex.Parse("0B 00 03 80 56 45 47 41 2D 31 00"), frame);
        Assert.Equal("VEGA-1", ControlFrames.Decode<DeviceLabel>(frame).Value);
    }

    [Fact]
    public void DeviceLabel_Limits()
    {
        Assert.Equal(32, new DeviceLabel(new string('x', 32)).Value.Length);
        Assert.Throws<ArgumentException>(() => new DeviceLabel(new string('x', 33)));
        Assert.Throws<ArgumentException>(() => new DeviceLabel("Вега"));
        Assert.Equal("", default(DeviceLabel).Value);
        Assert.Equal("AB", ControlFrames.Decode<DeviceLabel>(Hex.Parse("06 00 03 80 41 42")).Value);
    }
}
```

- [ ] **Step 3: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~VegaItemTests"`
Expected: збірка падає.

- [ ] **Step 4: Реалізувати `VegaProtocol`, переліки і п'ять структур за Interfaces**

- [ ] **Step 5: Запустити тести**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~VegaItemTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: Vega example protocol items" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Vega: емулятор, підключення і команди

**Files:**
- Create: `examples/Vega/NetSdr.Examples.Vega/VegaException.cs`, `VegaEvent.cs`, `VegaReceiver.cs`
- Create: `examples/Vega/NetSdr.Examples.Vega.Tests/VegaEmulator.cs`, `VegaFixture.cs`
- Test: `examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaConnectTests.cs`, `Receiver/VegaCommandTests.cs`

**Interfaces:**
- Consumes: пункти Vega (Task 10), `NetSdrControlClient`, `NetSdrTestServer`, `ControlReply`, стандартні `ProductId` і `ReceiverState` тощо.
- Produces:
  - `VegaReceiver` з API специфікації 11.3; у цій задачі все, крім `ReadEventsAsync`, `StartStreamAsync`, `StopStreamAsync` (вони в Task 12). `VegaException(string message)` успадковує `Exception`. `VegaEvent`, `TemperatureReport`, `OverloadDetected` як записи зі специфікації 11.3.
  - `VegaEmulator` з API специфікації 11.4, `DefaultKey = 0xC0DE_5EC5`.
  - Тести: `static class VegaFixture { static Task<(VegaEmulator Emulator, VegaReceiver Vega)> ConnectAsync(); }`. Він запускає емулятор і підключає `VegaReceiver` до `127.0.0.1:Port` з `DefaultKey`.

Правила емулятора (специфікація 11.4): `Preload(new ProductId(VegaProtocol.ProductId))`; обробники `VendorUnlock`, `AntennaSelect`, `BoardTemperature`, `DeviceLabel`, `OverloadEvent`; стан у полях емулятора під замком. `AntennaSelect`: Set зберігає порт для каналу і дає Echo, Get повертає `AntennaSelect(channel, порт або A)`. `BoardTemperature`: Get для заданого сенсора дає `Item`, для незаданого `Nak`; Set дає `Nak`. `DeviceLabel`: Set зберігає і дає Echo, Get повертає мітку (за замовчуванням `""`). `SendTemperatureAsync` і `SendOverloadAsync` викликають `Server.SendUnsolicitedAsync`.

Правила `VegaReceiver`: `ConnectAsync` створює клієнт, підключає, читає `ProductId`; чужий дає `VegaException($"Device is not a Vega receiver (product id 0x{id:X8}).")`; потім `SetAsync(new VendorUnlock(key))`. Будь-який виняток після створення клієнта закриває клієнт і пробрасується далі. `SetLabelAsync` створює `DeviceLabel` до надсилання, тож `ArgumentException` летить раніше за будь-який запит.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class VegaConnectTests
{
    [Fact]
    public async Task CorrectKey_Unlocks()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        Assert.True(emulator.IsUnlocked);
        Assert.Equal(new[] { (RequestType.Get, (ushort)0x0009), (RequestType.Set, VegaProtocol.VendorUnlockCode) },
            emulator.Server.Received.Select(r => (r.Type, r.Code)));
    }

    [Fact]
    public async Task WrongKey_ThrowsNak()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        var ex = await Assert.ThrowsAsync<NetSdrNakException>(
            () => VegaReceiver.ConnectAsync(new IPEndPoint(IPAddress.Loopback, emulator.Port), 0x1234));
        Assert.Equal(VegaProtocol.VendorUnlockCode, ex.Code);
        Assert.False(emulator.IsUnlocked);
    }

    [Fact]
    public async Task ForeignDevice_ThrowsAndClosesConnection()
    {
        await using var server = new NetSdrTestServer();
        server.Preload(new ProductId(0x03524453));
        await server.StartAsync();
        await Assert.ThrowsAsync<VegaException>(
            () => VegaReceiver.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), VegaEmulator.DefaultKey));
        Assert.DoesNotContain(server.Received, r => r.Code == VegaProtocol.VendorUnlockCode);
        await using var next = new NetSdrControlClient();       // server takes one client at a time,
        await next.ConnectAsync("127.0.0.1", server.Port);      // so this proves the first one closed
        Assert.Equal(0x03524453u, (await next.GetAsync<ProductId>().WaitAsync(Limits.Test)).Value);
    }

    [Fact]
    public async Task VendorCommand_BeforeUnlock_IsNak()
    {
        await using var emulator = new VegaEmulator();
        await emulator.StartAsync();
        await using var raw = new NetSdrControlClient();
        await raw.ConnectAsync("127.0.0.1", emulator.Port);
        await Assert.ThrowsAsync<NetSdrNakException>(() => raw.GetAsync<AntennaSelect, byte>(0));
    }
}

public class VegaCommandTests
{
    [Fact]
    public async Task Antenna_PerChannel()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        await vega.SelectAntennaAsync(0, AntennaPort.B);
        await vega.SelectAntennaAsync(1, AntennaPort.Loop);
        Assert.Equal(AntennaPort.B, await vega.GetAntennaAsync(0));
        Assert.Equal(AntennaPort.Loop, await vega.GetAntennaAsync(1));
        Assert.Equal(AntennaPort.A, await vega.GetAntennaAsync(2));
    }

    [Fact]
    public async Task Temperature_BySensor()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        emulator.SetTemperature(TemperatureSensor.Adc, 41.25);
        emulator.SetTemperature(TemperatureSensor.Board, -12.5);
        Assert.Equal(41.25, await vega.GetTemperatureAsync(TemperatureSensor.Adc));
        Assert.Equal(-12.5, await vega.GetTemperatureAsync(TemperatureSensor.Board));
        await Assert.ThrowsAsync<NetSdrNakException>(() => vega.GetTemperatureAsync(TemperatureSensor.Fpga));
    }

    [Fact]
    public async Task Label_RoundTrip()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        await vega.SetLabelAsync("Roof antenna");
        Assert.Equal("Roof antenna", await vega.GetLabelAsync());
    }

    [Fact]
    public async Task Label_TooLong_ThrowsBeforeSending()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        await Assert.ThrowsAsync<ArgumentException>(() => vega.SetLabelAsync(new string('x', 33)));
        Assert.DoesNotContain(emulator.Server.Received, r => r.Code == VegaProtocol.DeviceLabelCode);
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~VegaConnectTests|FullyQualifiedName~VegaCommandTests"`
Expected: збірка падає.

- [ ] **Step 3: Реалізувати `VegaException`, `VegaEvent`, `VegaReceiver` (частина цієї задачі), `VegaEmulator`, `VegaFixture`**

- [ ] **Step 4: Запустити тести**

Run: та сама команда, що в Step 2.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Vega receiver client and device emulator" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Vega: події і потік даних

**Files:**
- Modify: `examples/Vega/NetSdr.Examples.Vega/VegaReceiver.cs`
- Test: `examples/Vega/NetSdr.Examples.Vega.Tests/Receiver/VegaEventTests.cs`, `Receiver/VegaStreamTests.cs`

**Interfaces:**
- Consumes: `VegaReceiver`, `VegaEmulator`, `VegaFixture` (Task 11); `NetSdrDataReceiver`, `DataPacketInfo` (Task 8); `AutoStream` тестового сервера (Task 9).
- Produces: `IAsyncEnumerable<VegaEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct = default)`, `Task StartStreamAsync(IPEndPoint target, ulong frequencyHz, uint sampleRate, CancellationToken ct = default)`, `Task StopStreamAsync(CancellationToken ct = default)` за специфікацією 11.3.

Правила: `ReadEventsAsync` перебирає `Control.Unsolicited.ReadAllAsync(ct)`, бере лише `ReplyType.Unsolicited`, `BoardTemperature` перетворює на `TemperatureReport(Sensor, Celsius)`, `OverloadEvent` на `OverloadDetected(Channel, Flags)`; інші коди і `NetSdrProtocolException` з `As<T>` пропускає. Порядок і значення запитів `StartStreamAsync` і `StopStreamAsync` за специфікацією 11.3.

- [ ] **Step 1: Написати тести, що падають**

```csharp
public class VegaEventTests
{
    static async Task<List<VegaEvent>> TakeAsync(VegaReceiver vega, int count)
    {
        using var cts = new CancellationTokenSource(Limits.Test);
        var events = new List<VegaEvent>();
        await foreach (var e in vega.ReadEventsAsync(cts.Token))
        {
            events.Add(e);
            if (events.Count == count) break;
        }
        return events;
    }

    [Fact]
    public async Task Events_ArriveInOrder()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        await emulator.SendTemperatureAsync(TemperatureSensor.Board, 36.6);
        await emulator.SendOverloadAsync(1, OverloadFlags.Rf);
        Assert.Equal(new VegaEvent[] { new TemperatureReport(TemperatureSensor.Board, 36.6), new OverloadDetected(1, OverloadFlags.Rf) },
            await TakeAsync(vega, 2));
    }

    [Fact]
    public async Task EventDuringActiveRequest_DoesNotBreakIt()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        emulator.Server.OnRequest<BoardTemperature>(r =>
        {
            emulator.SendOverloadAsync(0, OverloadFlags.Adc).GetAwaiter().GetResult();
            return ControlReply.Item(BoardTemperature.FromCelsius(r.Key<TemperatureSensor>(), 30));
        });
        Assert.Equal(30.0, await vega.GetTemperatureAsync(TemperatureSensor.Board));
        Assert.Equal(new VegaEvent[] { new OverloadDetected(0, OverloadFlags.Adc) }, await TakeAsync(vega, 1));
    }

    [Fact]
    public async Task MalformedAndUnknown_AreSkipped()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        await emulator.Server.SendUnsolicitedAsync(VegaProtocol.BoardTemperatureCode, new byte[] { 1 });
        await emulator.Server.SendUnsolicitedAsync(0x9999, new byte[] { 1, 2 });
        await emulator.SendOverloadAsync(2, OverloadFlags.Adc);
        Assert.Equal(new VegaEvent[] { new OverloadDetected(2, OverloadFlags.Adc) }, await TakeAsync(vega, 1));
    }
}

public class VegaStreamTests
{
    [Fact]
    public async Task StartStream_DeliversCounterData_StopEndsIt()
    {
        var (emulator, vega) = await VegaFixture.ConnectAsync();
        await using var _ = emulator; await using var __ = vega;
        var packets = new ConcurrentQueue<(DataPacketInfo Info, byte[] Samples)>();
        using var receiver = new NetSdrDataReceiver((in DataPacketInfo info, ReadOnlySpan<byte> samples) =>
            packets.Enqueue((info, samples.ToArray())));
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.Start();

        await vega.StartStreamAsync(receiver.LocalEndPoint, 7_100_000, 200_000);
        await Eventually.ThatAsync(() => packets.Count >= 3);
        Assert.Equal(new ushort[] { 0x00B8, 0x0020, 0x00C5, 0x0018 },
            emulator.Server.Received.Skip(2).Select(r => r.Code));
        var first = packets.First();
        Assert.Equal((SampleFormat.Int16, (ushort)0), (first.Info.Format, first.Info.Sequence));
        Assert.Equal(new short[] { 0, -1, 1, -2 }, MemoryMarshal.Cast<byte, short>(first.Samples)[..4].ToArray());

        await vega.StopStreamAsync();
        await Task.Delay(100);
        var count = packets.Count;
        await Task.Delay(300);
        Assert.Equal(count, packets.Count);
    }
}
```

- [ ] **Step 2: Запустити й побачити падіння**

Run: `dotnet test NetSdr.sln --filter "FullyQualifiedName~VegaEventTests|FullyQualifiedName~VegaStreamTests"`
Expected: збірка падає, методів немає.

- [ ] **Step 3: Реалізувати `ReadEventsAsync`, `StartStreamAsync`, `StopStreamAsync`**

- [ ] **Step 4: Запустити всі тести рішення тричі поспіль**

Run: `dotnet test NetSdr.sln` (тричі)
Expected: PASS щоразу.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Vega events and streaming" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
