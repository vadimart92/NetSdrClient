# NetSdr

Клієнтська бібліотека .NET для приймачів NetSDR: канал керування по TCP, потік I/Q по UDP,
стійке перепідключення і перезавантаження пристрою. Як вона влаштована, описано в
[архітектурі](docs/architecture.md).

| Проект | Що це |
|---|---|
| `NetSdr` | Бібліотека, NuGet-пакет |
| `NetSdr.Testing` | Емулятор приймача (`NetSdrTestServer`) і сервісний канал (`SupervisionService`) для тестів застосунків, NuGet-пакет |
| `NetSdr.Tests` | Тести бібліотеки |
| `examples/Vega` | Приклад застосунку на бібліотеці, з власними тестами |

## Збірка

```bash
dotnet build NetSdr.slnx
```

## Тести

Тести запускаються на [Microsoft.Testing.Platform](https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-intro)
через NUnit runner (`global.json` вмикає його для `dotnet test`).

```bash
dotnet test --solution NetSdr.slnx
```

Звіт JUnit XML, окремий файл для кожного тестового проекту:

```bash
dotnet test --solution NetSdr.slnx --report-spekt-junit "LogFileName={assembly}.junit.xml"
```

Покриття коду (coverlet), звіти json і cobertura пишуться в `TestResults`:

```bash
dotnet test --solution NetSdr.slnx --coverlet
```

## Пакети

Версія обох пакетів задається у `NetSdrPackageVersion` в `Directory.Build.props`, версії залежностей
у `Directory.Packages.props`. Разом із `.nupkg` збираються пакети символів `.snupkg`.

```bash
dotnet pack NetSdr.slnx
```
