# Навигация по коду

## Запуск API

Начальная точка — `NutriFlow.Api/Program.cs`. В нём виден порядок регистрации сервисов, настройки HTTP и подключения маршрутов.

- `Configuration/ServiceRegistration.cs` — зависимости, пути хранения, HTTP-клиенты, выбор AI-провайдера и ограничитель запросов.
- `Configuration/ApplicationSetup.cs` — обработка ошибок, применение миграций при включённой настройке, демо-наполнение, защитные заголовки, статические файлы, Swagger и health checks.
- `Endpoints/MealSessionEndpoints.cs` — создание, чтение, дополнение, подтверждение сессии и отдельный разбор черновика.
- `Endpoints/DailyDiaryEndpoints.cs` — дневные цели и прогресс.
- `Endpoints/ProductEndpoints.cs` — продукты, штрихкоды и алиасы.
- `Endpoints/MediaEndpoints.cs` — расшифровка аудио, анализ и выдача фотографий.

Каждый файл endpoints сначала регистрирует маршруты, затем содержит их обработчики. URL, имена операций и JSON-контракты при разделении не менялись.

## От запроса к расчёту

```text
Program → Endpoints → MealWorkflowService
                          ├─ IMealParser → структурированный MealDraft
                          ├─ LocalProductCatalog → доступные продукты
                          ├─ Domain → расчёты КБЖУ
                          ├─ MealSessionStore → сохранённый предпросмотр
                          └─ DailyDiaryStore → подтверждённые порции
```

`Services/MealWorkflowService.cs` объединяет существующий сценарий: проверяет сообщения, вызывает парсер, разрешает продукты, строит предпросмотр, проверяет версию и подтверждает порции. Повторяющиеся продукты разрешаются один раз внутри одного расчёта, но заново при следующем запросе.

`Contracts` содержит тела запросов и ответов. `ResponseMapper.cs` преобразует доменные продукты, КБЖУ и порции в ответы API; бизнес-арифметики в нём нет.

## Домен и хранение

`NutriFlow.Domain` не зависит от HTTP, EF Core или AI-провайдера. Для расчётов начни с `NutritionValues`, затем `DishIngredient`, `DishBatch`, `MealEntry`, `DailyProgress`. Для ввода — `CaptureSession`, `MealDraft`, `DishDraft`, `IngredientDraft`, `PortionDraft`.

`NutriFlow.Infrastructure` содержит реализации внешних вызовов и хранения:

- `LocalProductCatalog` — личные и общие продукты, поиск, алиасы и повышение качества;
- `ProductLookupService` / `ExternalProducts` — local-first поиск по штрихкоду и внешний импорт;
- `MealSessionStore` — сессии, версии и ключи безопасных повторов;
- `MealDraftSerializer` — формат сохранённого JSON-черновика и восстановление доменных объектов с проверкой их ограничений;
- `DailyDiaryStore` — цели и атомарная запись порций;
- `Persistence` — EF-модель и история миграций;
- `Ai`, `Audio`, `LabelPhotos` — транспорт, проверка входных данных и файлов.

## Где проверять поведение

- `NutriFlow.Domain.Tests` — формулы, ограничения и неизменяемость модели.
- `NutriFlow.Infrastructure.Tests` — SQLite, владельцы, миграции, транзакции и внешние ответы на управляемых заменах HTTP.
- `NutriFlow.Api.Tests` — реальный ASP.NET Core pipeline с временной базой, маршруты, JSON, ошибки и повторы.
- `scripts/tests` — запись голоса и состояние временного клиента с заменами браузерных API.

`NutriFlow.Console` — отдельный исполняемый сценарий доменных расчётов. `wwwroot` — временный клиент API, не самостоятельное доменное ядро и не будущий мобильный клиент.
