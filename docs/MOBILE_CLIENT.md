# Мобильный клиент NutriFlow

## Текущее состояние

Выбран нативный .NET MAUI с C# и XAML. Исходники минимального клиента находятся в `mobile/NutriFlow.Mobile`, отдельное решение — `mobile/NutriFlow.Mobile.slnx`. Оно не включено в серверное `NutriFlow.sln`: сборка бэкенда и его CI не должны требовать мобильных SDK.

Подготовлены один общий статичный экран, точки входа Android / iOS, иконка и заставка. Сетевого клиента, дневника, входа, камеры и микрофона здесь пока нет. На 7 октября 2026 года Android-зависимости восстановлены и lock-файл проверен, но сборка остановилась на `XA5300`: отсутствует Android SDK, найденная Java 8 является JRE без инструмента `jar`. iOS-клиент успешно скомпилирован и упакован для ARM64-симулятора на облачном Mac: [run 37472323523](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37472323523), коммит `5100821`, 0 предупреждений / ошибок. Из его артефакта сохранён настоящий `packages.ios.lock.json`; [run 37648044029](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37648044029) на `fcc3876` успешно проверил уже отслеживаемый граф и повторил сборку / упаковку. Статусы всех шагов прочитаны через публичный GitHub API; полные текстовые логи вернули `403`, точные версии второго runner по ним не проверялись. Добавлена автоматизация первого запуска, но новый smoke ещё не выполнен в облаке: экран, устройство и подпись не проверены. APK / IPA для телефонов не получены. Windows по-прежнему не имеет iOS workload / Xcode; облачная компиляция не означает работоспособность приложения на телефоне.

## Что находится в проекте

- `NutriFlow.Mobile.csproj` — одна цель сборки за запуск: по умолчанию `net10.0-android` на Windows и `net10.0-ios` на macOS. Переданный `TargetFramework` имеет приоритет. Поэтому Android-сборке не нужна установленная iOS-нагрузка. `UseMaui` подключает инструменты MAUI, `SingleProject` исключает код чужой платформы при сборке. Минимальные версии ОС — Android 21 и iOS 15.0; это не версии SDK для компиляции.
- В том же `.csproj` заданы идентификатор приложения, версия, ресурсы и единственный явный NuGet-пакет `Microsoft.Maui.Controls`. `MauiVersion=10.0.0` задаёт версию MAUI. В Git сохранены отдельные `packages.android.lock.json` и `packages.ios.lock.json`; путь выбирается по платформе. iOS-файл получен из настоящего restore и успешной сборки, а не составлен вручную. Он фиксирует 20 пакетов: 2 прямых и 18 транзитивных, в том числе все 8 MAUI-пакетов версии `10.0.0`. Вторую прямую зависимость `Microsoft.NET.ILLink.Tasks` добавляет SDK, не пользовательский `PackageReference`. Поля `requested`, `resolved` и `contentHash` описывают запрошенный диапазон, выбранную версию и контрольную сумму пакета. `--locked-mode` проверяет граф вместо его молчаливого обновления. [Lock-файлы NuGet](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies).
- `MauiProgram.cs` — создаёт `MauiAppBuilder`, подключает `App` и собирает приложение. Здесь пока нет HTTP-клиента и дополнительных сервисов.
- `App.xaml` / `App.xaml.cs` — общие стили и создание окна с `MainPage`. Используется `CreateWindow`, а не устаревшее присваивание `Application.MainPage`.
- `MainPage.xaml` / `MainPage.xaml.cs` — разметка экрана и связанный C#-класс. Оба файла описывают один `partial`-класс; `InitializeComponent` подключает сгенерированную из XAML часть. На этом шаге в странице нет расчётов и обработчиков ввода.
- `Platforms/Android/MainApplication.cs` — связывает Android-приложение с `MauiProgram`; `MainActivity.cs` — стартовая Android activity с темой заставки.
- `Platforms/Android/AndroidManifest.xml` — настройки Android и ссылки на созданные иконки. Автоматическое резервное копирование приложения отключено; разрешения сети / камеры / микрофона пока не объявлены.
- `Platforms/iOS/Program.cs` — вызывает `UIApplication.Main`, передавая тип `AppDelegate`. iOS управляет жизненным циклом, а `AppDelegate.CreateMauiApp` передаёт создание общего приложения в `MauiProgram`.
- `Platforms/iOS/Info.plist` — объявляет приложение для iPhone / iPad, ориентации, ARM64 и каталог иконки. Идентификатор, версия и минимальная ОС берутся из `.csproj`. Разрешения микрофона / камеры и исключения для незашифрованного HTTP не добавлены.
- `Platforms/iOS/PrivacyInfo.xcprivacy` — минимальные причины обращения .NET / MAUI к времени файлов, времени работы системы и свободному месту. Файл явно включён как `BundleResource` только для iOS. Это не разрешение на сбор любых данных: при добавлении функций и SDK декларации нужно пересмотреть. [Privacy manifest в MAUI](https://learn.microsoft.com/en-us/dotnet/maui/ios/privacy-manifest?view=net-maui-10.0).
- `Resources/AppIcon` / `Resources/Splash` — самостоятельные SVG-ресурсы. Рисунок использует существующий знак NutriFlow, но не зависит от файлов веб-клиента или серверного проекта.

`.slnx` — XML-формат solution, поддерживаемый установленным .NET SDK. Он перечисляет мобильный проект; это не новое приложение и не отдельная библиотека. Серверное `.sln` не преобразовывалось. [Формат solution и команды CLI](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-sln).

## Подготовка Android на Windows

Уже установлен .NET SDK 10.0.400 и workload `maui-android` 10.0.0; .NET Android tools имеют версию 36.1.2. Workload добавляет средства .NET, но не заменяет Google Android SDK и Java JDK. Пользователь решил установить последние позже самостоятельно; установщик ниже агентом не запускался, лицензии Android не принимались.

На другом компьютере сначала установите SDK из `global.json`, затем нужную нагрузку:

```powershell
dotnet workload install maui-android --skip-manifest-update --source https://api.nuget.org/v3/index.json
dotnet workload list
```

Для редактора можно добавить расширение `.NET MAUI` в VS Code. Оно необязательно для CLI-сборки; здесь оно не устанавливалось. [Установка MAUI и инструментов Android](https://learn.microsoft.com/en-us/dotnet/maui/get-started/installation?view=net-maui-10.0&tabs=visual-studio-code).

Когда будете готовы скачать инструменты и принять условия лицензий, выполните из корня репозитория:

```powershell
$androidSdkDirectory = Join-Path $env:LOCALAPPDATA 'Android/Sdk'
$javaSdkDirectory = Join-Path $env:LOCALAPPDATA 'Microsoft/OpenJDK/jdk-21'

dotnet build mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj `
  -f net10.0-android -t:InstallAndroidDependencies `
  "-p:AndroidSdkDirectory=$androidSdkDirectory" `
  "-p:JavaSdkDirectory=$javaSdkDirectory" `
  -p:AcceptAndroidSDKLicenses=True
```

Это отдельная установка, а не обычная компиляция: команда скачивает Android SDK и JDK, а `AcceptAndroidSDKLicenses=True` означает согласие с лицензиями Android. Запускайте её только после ознакомления и согласия. Проверенный `GetAndroidDependencies` для текущего проекта перечислил Android Platform 36, Build Tools 36.0.0, Platform Tools и Command-line Tools 19.0; эмулятора и NDK в этом списке нет. Инсталлятор выбирает JDK 21 согласно установленным Android targets. Не подменяйте JDK старым Java 8 JRE. [Назначение InstallAndroidDependencies](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-targets#installandroiddependencies).

Если SDK/JDK уже установлены другим способом, пропустите установку и укажите их настоящие корневые каталоги в этих двух переменных. Не сохраняйте абсолютные пользовательские пути в `.csproj`. Переменные действуют в текущем PowerShell-терминале и нужны в командах ниже; команда не меняет `JAVA_HOME` или PATH всей системы.

## Проверка и пробное APK после установки

```powershell
dotnet restore mobile/NutriFlow.Mobile.slnx --locked-mode --warnaserror

dotnet build mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj `
  -f net10.0-android --configuration Debug --no-restore `
  -t:SignAndroidPackage `
  "-p:AndroidSdkDirectory=$androidSdkDirectory" `
  "-p:JavaSdkDirectory=$javaSdkDirectory" `
  -p:AndroidRestoreOnBuild=false -p:EmbedAssembliesIntoApk=true
```

`SignAndroidPackage` создаёт подписанное APK; `EmbedAssembliesIntoApk=true` делает Debug-пакет пригодным для отдельной установки без fast deployment через IDE. Это пробная Debug-сборка со стандартным debug-ключом, не релиз для распространения. После успешной сборки ищите `*-Signed.apk` в `mobile/NutriFlow.Mobile/bin/Debug/net10.0-android/`. Сейчас такого проверенного артефакта нет. [Android build targets](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-targets#signandroidpackage), [правила подписи](https://learn.microsoft.com/en-us/dotnet/maui/android/deployment/publish-cli?view=net-maui-10.0).

Для первого запуска предпочтителен настоящий Android-телефон: эмулятор пока не нужен. Установка APK, разрешение установки из выбранного источника и проверка экрана требуют участия пользователя. Не создавайте релизный keystore внутри репозитория и не коммитьте его; выпуск и подпись релизных обновлений — отдельный шаг.

## Что скачать

### На текущем Windows-компьютере для Android

1. **JDK 21 x64**, не Java 8 JRE: [Microsoft Build of OpenJDK](https://learn.microsoft.com/en-us/java/openjdk/download).
2. **Android SDK**: [официальная страница Android Studio и Command-line tools](https://developer.android.com/studio). Для нашего первого APK достаточно Command-line tools, Platform 36, Build Tools 36.0.0 и Platform Tools; полный Android Studio, эмулятор и NDK пока не нужны. Можно вместо ручной установки использовать приведённый выше `InstallAndroidDependencies`, который скачивает и SDK, и подходящий JDK.
3. Необязательно — [расширение .NET MAUI для VS Code](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.dotnet-maui). .NET SDK и `maui-android` на этом компьютере уже установлены, повторно скачивать их не требуется.

### На Mac для iOS

1. **Доступ к Mac** — своему или удалённому. Xcode и iOS SDK не устанавливаются как обычные Windows-инструменты; наличие iPhone этого требования не отменяет.
2. **Совместимые macOS и Xcode**. Базовый iOS-манифест этой установки — `26.0.11017`, его проверяемая исходная связка — Xcode 26.0 на macOS Sequoia 15.6 или новее. Xcode доступен в [каталоге загрузок Apple](https://developer.apple.com/download/all/). Не выбирайте самую новую версию вслепую: сверяйте её с установленным .NET iOS workload. [Требования выпуска .NET 10 для Apple](https://github.com/dotnet/macios/wiki/.NET-10-release-notes), [таблица совместимости Xcode](https://developer.apple.com/xcode/system-requirements/).
3. **.NET SDK 10.0.401 для macOS**, использованный успешной облачной сборкой, из [официальных загрузок .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0): Arm64 для Apple Silicon, x64 для Intel. `global.json` задаёт `10.0.400` с `latestPatch`, поэтому допускает `10.0.401`; это не строгая фиксация всех инструментов. iOS lock содержит `Microsoft.NET.ILLink.Tasks` `10.0.12`, Android lock — `10.0.11` от локального SDK `10.0.400`. Другой SDK может потребовать пересмотра неявных зависимостей: проверяйте `dotnet --version` и не исправляйте lock вручную ради прохождения restore. [Выбор SDK](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json#rollforward).
4. **Нагрузка `maui-ios`** командой ниже. После первого запуска Xcode установите предлагаемые компоненты iOS / симулятора и ознакомьтесь с лицензиями. Нужные компоненты Apple и .NET workload — разные установки.
5. Необязательно — VS Code с расширением `.NET MAUI` по ссылке выше. Для CLI-сборки расширение не требуется.

Для симулятора платная Apple Developer Program не нужна. Для установки на iPhone потребуются подпись, настройка устройства и Apple Account. Бесплатный Personal Team использует профили на семь дней, затем приложение нужно заново собрать / установить; это не постоянная установка. Платные варианты распространения без публикации в App Store рассматриваются отдельно. Сертификаты `.p12` и provisioning profiles исключены из Git / Docker, но не созданы и не настроены. [Ограничения Personal Team](https://developer.apple.com/help/account/basics/about-your-developer-account).

## Первая проверка iOS на Mac

Откройте Xcode хотя бы один раз и завершите его первичную настройку самостоятельно. Следующие команды выполняются в терминале **Mac**, из корня репозитория. Успешная облачная связка — Xcode 26.0.1, .NET SDK 10.0.401 и workload set 10.0.100. На Windows-ноутбуке эти Apple-инструменты не устанавливались.

```bash
dotnet --info
xcodebuild -version
xcode-select -p
dotnet workload install maui-ios --version 10.0.100 --source https://api.nuget.org/v3/index.json
dotnet workload list
dotnet workload --version
```

Сопоставьте фактическую версию iOS workload с требованиями к Xcode, прежде чем собирать. `--version 10.0.100` закрепляет согласованный workload set: MAUI 10.0.0 / iOS 26.0.11017. Версия этого набора не обязана совпадать с выбранным SDK 10.0.400. `--skip-manifest-update` без явной версии сохранял бы уже имеющиеся манифесты машины, которые в облаке могут быть другими. Для защищённого каталога SDK на Mac команда установки требует `sudo`. [Workload sets](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-sets).

Для Apple Silicon используйте уже сохранённый iOS lock-файл. Обычная проверка начинается сразу с locked restore; во всех командах указан один и тот же runtime identifier:

```bash
dotnet restore mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj \
  -p:TargetFramework=net10.0-ios --runtime iossimulator-arm64 --locked-mode --warnaserror

dotnet build mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj \
  -f net10.0-ios --runtime iossimulator-arm64 --configuration Debug --no-restore \
  -p:EnableCodeSigning=false
```

Lock-файл содержит `net10.0-ios26.0` и секцию `net10.0-ios26.0/iossimulator-arm64`; пустая RID-секция означает отсутствие дополнительных пакетов поверх основного графа, не отсутствие зависимостей. Для Intel Mac нужен `iossimulator-x64`, для iPhone — `ios-arm64`: их графы и сборки этим файлом / запуском не проверены. Смена RID, SDK или зависимостей — отдельное осознанное обновление: выполните настоящий restore для выбранной конфигурации без `--locked-mode`, проверьте diff и повторите locked restore. Не заменяйте версии и хэши вручную и не отключайте locked mode в CI ради ошибки. Это сборка **для симулятора**, не IPA; отключение подписи нельзя переносить в инструкцию установки на телефон. Проверка запуска описана ниже; подпись остаётся отдельной задачей.

Чтобы на Mac выбрать Android явно, передавайте `-p:TargetFramework=net10.0-android` в restore и `-f net10.0-android` в build. Android SDK/JDK в таком случае нужны и на Mac.

## Облачная проверка iOS без собственного Mac

`.github/workflows/ios-simulator.yml` добавляет отдельный ручной workflow **iOS simulator**. Windows-компьютеру для него не нужны Xcode, iOS workload или Android SDK. GitHub предоставляет облачную машину, которая получает отслеживаемый код репозитория; локальные данные, User Secrets и приватные файлы с ноутбука туда не отправляются. Основной серверный CI не менялся.

- `workflow_dispatch` означает запуск кнопкой, не при каждом push / pull request. `runner` — временная машина GitHub, исполняющая команды.
- Выбран стандартный ARM64 runner `macos-15`, явно задан Xcode 26.0.1 вместо его устаревшего Xcode по умолчанию. SDK берётся из `global.json`, workload set закреплён на `10.0.100`. Наличие Xcode и архитектура проверяются до сборки. Если GitHub удалит эту версию из образа, workflow завершится с понятной ошибкой; автоматически переходить на несовместимый Xcode он не будет. [Состав ARM64-образа](https://github.com/actions/runner-images/blob/main/images/macos/macos-15-arm64-Readme.md).
- `packages.ios.lock.json` уже сохранён в Git, поэтому workflow выполняет только locked restore для того же `iossimulator-arm64`: CI не должен молча переписывать зависимости. Резервная ветка первого restore используется только при отсутствии файла. Проверяется и неизменность Android lock-файла. Установка через `sudo` и обычный restore используют разные HTTP-кэши NuGet; это исправило наблюдавшийся отказ доступа в первом запуске.
- Выполняется Debug-сборка для симулятора с отключённой подписью, затем `plutil` проверяет уже упакованные `Info.plist` и `PrivacyInfo.xcprivacy`, наличие исполняемого файла проверяется отдельно. Перед установкой workload запускаются тесты smoke-скрипта с заменами Apple-команд; они не требуют загрузки симулятора.
- После упаковки и загрузки результата `scripts/test-ios-simulator.mjs` создаёт отдельный симулятор по типу доступного iPhone с runtime iOS 26.0. Он читает bundle ID из собранного plist, ждёт boot, устанавливает / запускает `.app`, трижды сверяет положительный PID и label приложения внутри выбранного симулятора. Между проверками сохраняется `startup.png`; интервалы ожидания — 10 и 5 секунд. Ни Apple-аккаунт, ни окно Simulator.app не нужны. `simctl` — CLI Xcode для управления симуляторами, `launch` запускает приложение через систему, а `spawn` выполняет диагностическую команду внутри симулятора. [CLI Apple](https://developer.apple.com/videos/play/wwdc2019/418/), [захват без окна Simulator.app](https://developer.apple.com/videos/play/wwdc2020/10647/).
- Каждая команда имеет предел времени; boot — до 240 секунд, install / launch — до 60. Smoke-step ограничен 15 минутами, его бюджет проверяется тестом с резервом на диагностику / cleanup. Скрипт удаляет только полученный от собственного `create` UUID. Нет удаления всех симуляторов, автоматической загрузки отсутствующего runtime или полного `simctl diagnose`. Ошибка записи логов не блокирует cleanup и не скрывает исходный сбой. `finally` помогает при обычных ошибках, но не гарантирует выполнение при принудительной отмене / остановке job; облачная VM временная.
- `artifact` — сохранённый результат job. Прежний `nutriflow-ios-simulator-<run id>-<attempt>` содержит только `NutriFlow-ios-simulator-arm64.tar.gz` и `packages.ios.lock.json`; `.app` помещён в tar для сохранения прав. Новый `nutriflow-ios-startup-<run id>-<attempt>` содержит PNG, `result.json`, журнал команд, справку установленного `simctl`, stdout / stderr приложения и ограниченные системные логи его процесса. Диагностика загружается и при провале smoke, если step выполнялся и job не отменён; недоступные файлы не выдумываются. Архив сборки может существовать при красном smoke: он не доказывает успешный запуск. Срок хранения обоих — семь дней. Ни IPA, ни сертификаты, ни полный рабочий каталог не публикуются.
- Права workflow ограничены `contents: read`, Git-учётные данные не сохраняются после checkout. Apple-аккаунты / секреты и изменение настроек репозитория не нужны. Job ограничен 30 минутами, новый ручной запуск той же ветки отменяет предыдущий.

Репозиторий `Vvvv4a40/NutriFlow` на момент подготовки публичный; стандартные GitHub-hosted runners бесплатны для публичных репозиториев. Это не обещание бесплатной подписи Apple и не относится к larger runners. Для приватных репозиториев нужно учитывать квоты / расходы; видимость репозитория этим шагом не менялась. [Правила GitHub](https://docs.github.com/en/actions/how-tos/write-workflows/choose-where-workflows-run/choose-the-runner-for-a-job).

### Как запустить и прочитать результат

1. Откройте [iOS simulator в GitHub Actions](https://github.com/Vvvv4a40/NutriFlow/actions/workflows/ios-simulator.yml) под своим аккаунтом.
2. Нажмите **Run workflow**, выберите `main`, подтвердите запуск. Файл должен уже находиться в default branch; кнопка требует права записи в репозиторий. [Ручной запуск Actions](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).
3. Дождитесь завершения job. При ошибке откройте первый красный step и сохраните текст ошибки; успешный upload не подменяет успешную сборку / запуск. Проверьте шаг **Launch app in isolated iOS simulator**.
4. Скачайте `nutriflow-ios-startup-<run id>-<attempt>`, если он появился. После успеха откройте `startup.png`: ожидаются заголовок NutriFlow и первый статичный экран, не домашний экран iOS / заставка. Положительный PID и PNG сами по себе не доказывают, что нужная страница видна. Передайте ссылку на run и снимок; при ошибке — `result.json`, `commands.log`, `app-stderr.log` и `app-system.log`, если они получены.
5. Артефакт сборки сохраняется отдельно. Lock-файл уже проверен повторным run; теперь нужен новый `Run workflow` на актуальном `main`, не `Re-run jobs` старого коммита, чтобы проверить добавленный smoke. При обновлении зависимостей отдельно сравните новый artifact с отслеживаемым lock. Workflow не создаёт коммиты сам.

### Локальная проверка автоматизации и запуск на Mac

На Windows можно проверить только управление командами на искусственных ответах:

```powershell
node --check scripts/test-ios-simulator.mjs
node --test scripts/tests/ios-simulator.test.mjs
```

32 теста проверяют последовательность / выбранный UUID, PID другого процесса / приложения, раннее завершение, ошибки / таймаут команд, потерю каталога логов, PNG и cleanup. Пакеты npm не устанавливаются: используются стандартные модули Node.js. Серверный CI уже подхватывает эти тесты существующим wildcard; его YAML не менялся. Это тесты CI-инструмента, не подключение JavaScript к MAUI и не настоящая iOS-проверка.

После успешной сборки выше на Apple Silicon Mac с доступным iOS 26.0 runtime можно выполнить тот же скрипт. Установленный Node.js нужен только для этой автоматизации. Скрипт требует новый выходной каталог, чтобы не принять старый screenshot за новый результат:

```bash
smokeRoot="$(mktemp -d)"
node scripts/test-ios-simulator.mjs \
  mobile/NutriFlow.Mobile/bin/Debug/net10.0-ios/iossimulator-arm64/NutriFlow.Mobile.app \
  "$smokeRoot/startup"
```

Проверка завершается ненулевым кодом при сбое запуска / процесса / снимка / cleanup. Скриншот просматривается отдельно. На текущем Windows этот настоящий запуск невозможен; инструменты не устанавливались. Он не проверяет API, пользовательские сценарии, минимальную iOS 15, все модели устройств, подпись или собственный iPhone.

**Архив симулятора нельзя установить на iPhone и нельзя считать IPA для AltStore.** Получение пакета для настоящего `ios-arm64`, проверка AOT / подписи, установка и обновление через выбранный способ — отдельный следующий шаг. Не передавайте Apple-пароль агенту и не сохраняйте сертификаты / пароли в YAML или Git.

## Границы клиента

Мобильный проект не ссылается на `Api`, `Infrastructure` или `Domain`. Сервер остаётся единственным источником расчётов, хранения и AI-интеграций; ключ Groq не переносится на телефон. Следующий шаг после первого запуска — небольшой HTTP-клиент и проверка capabilities по [MOBILE_API.md](MOBILE_API.md), без всех экранов сразу. На телефоне `localhost` означает телефон, не компьютер с API.

Подготовка iOS добавлена по прямому запросу пользователя до завершения Android-сборки. Она ограничена точкой входа, настройками платформы и инструкцией: следующие экраны и API-клиент не реализуются заранее. Общие C# / XAML не отменяют отдельных сборок и проверки каждой платформы. [Требования к платформам MAUI](https://learn.microsoft.com/en-us/dotnet/maui/get-started/installation?view=net-maui-10.0&tabs=visual-studio-code).
