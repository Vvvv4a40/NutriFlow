# NutriFlow

Дневник питания, который собирает блюдо из свободного описания: текста, голоса, фото этикетки или штрихкода. AI превращает ввод в структурированный черновик, а калории и БЖУ считает обычный C#-код на `decimal`. Перед записью в дневник всегда есть предпросмотр и явное подтверждение.

## Возможности

- Продукты: ручной ввод, фото этикетки, штрихкод (Open Food Facts), источник и точность данных у каждого значения.
- Собственные блюда из нескольких ингредиентов с учётом готового веса и порции из них.
- Дневная цель по калориям и БЖУ, исправление и удаление записей.
- Android-приложение работает без сервера: локальные профили, SQLite, резервная копия профиля в ZIP.
- Текст, этикетки и голос разбираются через Groq (личный ключ пользователя).

## Стек

.NET 10, .NET MAUI (Android, iOS), ASP.NET Core, EF Core + SQLite, xUnit.

## Структура

| Проект | Назначение |
| --- | --- |
| `NutriFlow.Domain` | Расчёт пищевой ценности, черновики блюд, дневной прогресс |
| `NutriFlow.Infrastructure` | SQLite, каталог продуктов, клиенты Groq / OpenAI / Open Food Facts, резервные копии |
| `NutriFlow.Api` | HTTP API и простой веб-клиент |
| `mobile/NutriFlow.Mobile.Core` | Логика автономного мобильного клиента |
| `mobile/NutriFlow.Mobile` | MAUI-приложение |
| `NutriFlow.Tools` | Резервное копирование и восстановление данных сервера |

## Сборка и тесты

```powershell
dotnet test NutriFlow.sln
dotnet test mobile/NutriFlow.Mobile.Core.slnx
node --test scripts/tests/*.test.mjs
```

Android (нужны Android SDK и JDK):

```powershell
dotnet build mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj -f net10.0-android -c Debug
dotnet publish mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj -f net10.0-android -c Release
```

Debug-сборка ставится как отдельное приложение `com.nutriflow.dev`, Release собирает AAB и APK.

## Сервер

```powershell
dotnet run --project NutriFlow.Api
```

В окружении Development используется тестовый парсер без внешних запросов. Для настоящего AI задайте `Ai:Provider` (`Groq` или `OpenAI`) и ключ `Ai:Groq:ApiKey` / `Ai:OpenAI:ApiKey` через переменные окружения или user secrets.

Данные о продуктах по штрихкоду — [Open Food Facts](https://world.openfoodfacts.org/), лицензия ODbL.
