# Modulith

Модульный монолит на ASP.NET Core — без фреймворка.

[English version](README.md)

Модуль — это обычная библиотека классов. Она активируется тем, что её сборку назвали в
`HOSTINGSTARTUPASSEMBLIES`. Один образ, любая топология развёртывания, выбираемая переменной
окружения, а не пересборкой. В ASP.NET Core все нужные механизмы есть уже много лет; этот пакет —
один базовый класс поверх них плюс анализаторы, которые не дают архитектуре протечь.

```csharp
// OrdersModule/Module.cs — весь контракт модуля
[assembly: HostingStartup(typeof(Module))]

class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
        => services.AddScoped<IOrderService, OrderService>();

    protected override void Configure(IApplicationBuilder app)
        => app.UseEndpoints(e => e.MapGroup("/orders").MapGet("/unpaid", (IOrderService s) => s.Unpaid()));
}
```

```csharp
// хост — обратите внимание, что он не знает ни об одном модуле
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseRouting();
app.Run();
```

```yaml
# один образ, три сервиса
orders:    { environment: { HOSTINGSTARTUPASSEMBLIES: "Orders.Entities;OrdersModule" } }
customers: { environment: { HOSTINGSTARTUPASSEMBLIES: "Customers.Entities;CustomersModule" } }
monolith:  { environment: { HOSTINGSTARTUPASSEMBLIES: "Orders.Entities;Customers.Entities;Monolith" } }
```

## Установка

```
dotnet add package Modulith
```

Это вся зависимость. Анализаторы идут вместе с ней. `net8.0` и `net10.0`.

## Зачем вообще пакет

Потому что у подхода есть острые углы, и каждый из них отказывает молча:

- Модуль, забывший `[assembly: HostingStartup]`, загружается и ничего не делает. Приложение
  стартует, readiness-проба проходит, а первый признак — 404. **MOD0005** превращает это в ошибку
  сборки.
- Опечатка в `HOSTINGSTARTUPASSEMBLIES` заставляет ASP.NET Core написать critical в лог и
  продолжить старт. `ModuleBase` вместо этого роняет запуск.
- Razor SDK подключает контроллеры модуля к хосту в обход `HOSTINGSTARTUPASSEMBLIES`, поэтому они
  появляются в топологиях, где модуль исключили намеренно. MSBuild-ассеты это отключают,
  **MOD0004** страхует.
- Использование хостом одного типа из одного модуля тихо помещает этот модуль в каждое
  развёртывание. **MOD0003** это ловит, оставляя ссылку на проект, которая модели нужна.
- Имена модулей — это строки в переменной окружения, поэтому переименование обнаруживается в
  проде. `KnownModules` генерируется из ваших ссылок на проекты и делает это ошибкой компиляции.

## Правила

| | |
|---|---|
| [MOD0001](docs/rules/MOD0001.md) | Модуль не должен содержать публичных типов |
| [MOD0002](docs/rules/MOD0002.md) | Тип, указанный в HostingStartup, должен быть модулем |
| [MOD0003](docs/rules/MOD0003.md) | Хост не должен использовать типы модулей |
| [MOD0004](docs/rules/MOD0004.md) | Хост не должен объявлять ApplicationPart для модуля |
| [MOD0005](docs/rules/MOD0005.md) | Модуль должен быть указан в атрибуте HostingStartup уровня сборки |
| [MOD0006](docs/rules/MOD0006.md) | Модуль не должен подменять пайплайн приложения |
| [MOD0007](docs/rules/MOD0007.md) | Лишняя регистрация IStartupFilter |
| [MOD0008](docs/rules/MOD0008.md) | Hosted-сервис в модуле выполняется в каждой реплике, которая его загрузила |
| [MOD0020](docs/rules/MOD0020.md) | Пакет Modulith не подключён |

Диагностики выводятся на английском и на русском — Roslyn берёт язык из текущей UI-культуры, так
что в IDE вы увидите русский, а в логе CI английский.

## Примеры

| | |
|---|---|
| [01 — Minimal API](samples/01-minimal-api) | Самое маленькое, что показывает идею |
| [02 — MVC и Razor Pages](samples/02-mvc-razor) | Вьюхи, области, page models и статика по модулям |
| [03 — Модульная модель данных](samples/03-modular-data) | Одна модель EF Core, собранная из модулей; три топологии из одного образа |

## Миграция существующей системы

В [`skill/`](skill) лежат инструкции для ИИ-агента, который делает миграцию: обследование, хост,
модули, данные, транспорты, развёртывание и цикл верификации, выполняемый после каждой фазы, а не
в конце. Пишется один раз и генерируется в плагин Claude Code, портируемый
[`SKILL.md`](skill/dist/SKILL.md) для любого другого инструмента и короткий always-on файл правил.

Текст на английском, и его стоит прочитать как прозу, даже если мигрируете руками — особенно
[troubleshooting](skill/src/references/troubleshooting.md): там отказы этого подхода перечислены
по симптомам, потому что все они молчаливые.

## Чего здесь намеренно нет

Пакета для EF Core, пакета для тестирования, шаблонов проектов, абстракции над транспортом.
Каждый из них был бы десятком строк, завёрнутым в то, от чего надо зависеть, что надо
версионировать и изучать, — а утверждение этого проекта в том, что подходу фреймворк не нужен.
Всё это живёт в примерах как код, который копируют, с объяснением рядом:
[рецепт модели данных](samples/03-modular-data#the-recipe),
[рецепт тестирования](samples/03-modular-data#tests).

И ещё: модули грузятся по имени в рантайме, поэтому `PublishTrimmed` и `PublishAot` для хоста
недоступны. Это цена подхода, и знать о ней лучше до внедрения.

## Статус

Ранняя разработка, до 1.0. Публичный API не зафиксирован, имя пакета на nuget.org ещё не занято.

## Происхождение

Извлечено из доклада на DotNext «Модульность без микросервисов»
([исходники](https://github.com/gandjustas/dotnext-2026)).

## Лицензия

MIT
