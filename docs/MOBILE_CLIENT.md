# Мобильный клиент NutriFlow

## Текущее состояние

Выбран нативный .NET MAUI с C# и XAML. Исходники минимального клиента находятся в `mobile/NutriFlow.Mobile`, отдельное решение — `mobile/NutriFlow.Mobile.slnx`. Оно не включено в серверное `NutriFlow.sln`: сборка бэкенда и его CI не должны требовать мобильных SDK.

Подготовлены один общий статичный экран, точки входа Android / iOS, иконка и заставка. Сетевого клиента, дневника, входа, камеры и микрофона здесь пока нет. На 8 октября 2026 года Android-зависимости восстановлены и lock-файл проверен, но сборка остановилась на `XA5300`: отсутствует Android SDK, найденная Java 8 является JRE без инструмента `jar`. iOS-клиент успешно скомпилирован и упакован для ARM64-симулятора на облачном Mac: [run 37472323523](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37472323523), коммит `5100821`, 0 предупреждений / ошибок. Из его артефакта сохранён настоящий `packages.ios.lock.json`; [run 37648044029](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37648044029) на `fcc3876` успешно проверил уже отслеживаемый граф и повторил сборку / упаковку.

[Run 37755718947](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37755718947) прошёл загрузку iOS за 51 секунду, открытие / закрытие «Настроек» с лимитом 120 секунд и очистку собственного GUI / UUID. NutriFlow установлен, команда `launch` вернула PID 5909, но процесс исчез до первой проверки. В `launch-system.log` система указала `codesigning / invalid-page`; ранее там же зафиксирована ошибка проверки ресурса заставки. Категория завершения подтверждена, конкретная неверная страница и её причина пока неизвестны. Это не доказанное C#-исключение.

[Run 37761033647, attempt 2](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37761033647/attempts/2) на HEAD `2870d45ebc82a8f7f5c67b2a2d3b3c8f74748d2e` завершился успешно. Строгая SDK-подпись / boot / «Настройки» / NutriFlow / cleanup прошли; smoke занял 6 минут 49 секунд. `startup.png` визуально проверен: заголовок NutriFlow, «Учёт питания без лишней рутины» и «Подключение к серверу пока не настроено». Это именно общий статичный экран, не Settings, home или splash. Проверена iOS 26.0.1 / iPhone 17 Pro в ARM64-симуляторе. Устройство и APK / IPA для телефонов не проверены. Windows по-прежнему не имеет iOS workload / Xcode; успешный симуляторный запуск не означает готовую установку на iPhone или работающие мобильные API-сценарии.

У этого run первая попытка остановилась на завершении «Настроек» (`readiness-terminate`, лимит 30 секунд), до установки NutriFlow. Контролируемый повтор выполнен на том же HEAD без изменения исходников или тайм-аутов; он не является новой автоматической retry-политикой. Обычный серверный CI на том коммите остановился из-за прежней Linux-ошибки выбора `dotnet` в `Test-MealWorkflow.ps1`, не из-за iOS-подписи. Она исправлена следующим отдельным шагом: выбирается первый найденный исполняемый файл, добавлены четыре регрессии. [CI 37769795754](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37769795754) на `2b33cff` прошёл полностью, включая отдельный API / перезапуск, публикацию, EF, аудит пакетов и Docker. Это не сборка / подпись IPA и не проверка мобильных HTTP-сценариев.

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
  -p:EnableCodeSigning=true -p:CodesignKey=-
```

Lock-файл содержит `net10.0-ios26.0` и секцию `net10.0-ios26.0/iossimulator-arm64`; пустая RID-секция означает отсутствие дополнительных пакетов поверх основного графа, не отсутствие зависимостей. Для Intel Mac нужен `iossimulator-x64`, для iPhone — `ios-arm64`: их графы и сборки этим файлом / запуском не проверены. Смена RID, SDK или зависимостей — отдельное осознанное обновление: выполните настоящий restore для выбранной конфигурации без `--locked-mode`, проверьте diff и повторите locked restore. Не заменяйте версии и хэши вручную и не отключайте locked mode в CI ради ошибки.

`EnableCodeSigning=true` включает штатную подпись SDK, `CodesignKey=-` задаёт ad-hoc identity без Apple-сертификата. Это не Apple Ad Hoc Distribution и не способ установить приложение на iPhone. В [закреплённом DetectSigningIdentity](https://github.com/dotnet/macios/blob/dotnet-10.0.1xx-xcode26.0-11017/msbuild/Xamarin.MacDev.Tasks/Tasks/DetectSigningIdentity.cs#L594) `-` принимается до поиска сертификата / профиля. SDK сам [собирает вложенный нативный код](https://github.com/dotnet/macios/blob/dotnet-10.0.1xx-xcode26.0-11017/msbuild/Xamarin.MacDev.Tasks/Tasks/ComputeCodesignItems.cs#L109) и [подписывает его раньше бандла](https://github.com/dotnet/macios/blob/dotnet-10.0.1xx-xcode26.0-11017/msbuild/Xamarin.MacDev.Tasks/Tasks/Codesign.cs#L479). Ручная переподпись `--deep --force`, отключение проверки подписи / профиля и изменение entitlements не добавляются. Эти флаги ограничены симуляторным workflow и приведённой командой; физический `ios-arm64` требует отдельной настройки.

Чтобы на Mac выбрать Android явно, передавайте `-p:TargetFramework=net10.0-android` в restore и `-f net10.0-android` в build. Android SDK/JDK в таком случае нужны и на Mac.

## Облачная проверка iOS без собственного Mac

`.github/workflows/ios-simulator.yml` добавляет отдельный ручной workflow **iOS simulator**. Windows-компьютеру для него не нужны Xcode, iOS workload или Android SDK. GitHub предоставляет облачную машину, которая получает отслеживаемый код репозитория; локальные данные, User Secrets и приватные файлы с ноутбука туда не отправляются. Основной серверный CI не менялся.

- `workflow_dispatch` означает запуск кнопкой, не при каждом push / pull request. `runner` — временная машина GitHub, исполняющая команды.
- Выбран стандартный ARM64 runner `macos-15`, явно задан Xcode 26.0.1 вместо его устаревшего Xcode по умолчанию. SDK берётся из `global.json`, workload set закреплён на `10.0.100`. Наличие Xcode и архитектура проверяются до сборки. Если GitHub удалит эту версию из образа, workflow завершится с понятной ошибкой; автоматически переходить на несовместимый Xcode он не будет. [Состав ARM64-образа](https://github.com/actions/runner-images/blob/main/images/macos/macos-15-arm64-Readme.md).
- `packages.ios.lock.json` уже сохранён в Git, поэтому workflow выполняет только locked restore для того же `iossimulator-arm64`: CI не должен молча переписывать зависимости. Резервная ветка первого restore используется только при отсутствии файла. Проверяется и неизменность Android lock-файла. Установка через `sudo` и обычный restore используют разные HTTP-кэши NuGet; это исправило наблюдавшийся отказ доступа в первом запуске.
- Сначала выполняется исходная Debug-сборка с `EnableCodeSigning=false`. Read-only скрипт `verify-ios-simulator-signature.mjs` запускается с `--observe`: неверная подпись допускается только для сохранения исходных данных, но отчёт честно остаётся `invalid`. Затем SDK повторно собирает тот же RID / Debug с `EnableCodeSigning=true` / `CodesignKey=-` и выполняется строгая проверка без `--observe`. Ненулевой итог останавливает сценарий до упаковки и установки. Отдельный `plutil` проверяет `Info.plist` / `PrivacyInfo.xcprivacy`. Наличие подписи от линкера само по себе не доказывает её корректность; сравнение before / after должно показать фактическую разницу. Перед workload запускаются Node-тесты smoke / signature с заменами Apple-команд, без настоящего симулятора.
- Проверка подписи читает идентификатор `com.nutriflow.app` и платформу `iPhoneSimulator` из манифеста, требует главный Mach-O ARM64 / `IOSSIMULATOR` через `lipo` / `vtool`. Обход бандла находит Mach-O по заголовку, не пытаясь подписывать managed DLL. Для каждого нативного файла выполняется `codesign --verify --strict`, для бандла — `--verify --deep --strict`; ожидается `Signature=adhoc`. Здесь `--deep` означает рекурсивную проверку, не переподпись. На инструмент отведено до 15 секунд, на весь скрипт — до 120; тайм-ауты, неверный манифест / платформа и ошибки запуска инструментов не допускаются даже в `--observe`. Скрипт не меняет `.app`; новый каталог отчёта обязан находиться вне бандла. Логи команд, инвентарь / результаты и `result.json` позволяют проверить каждую операцию. Валидная подпись ещё не доказывает первый экран: после неё остаётся прежний native smoke.
- В обеих попытках run 37761033647 сравнение подписи прошло одинаково: before — `invalid`, 12 нативных файлов, linker-signed главный файл и отсутствие запечатанных ресурсов; after — `passed`, все 12 нативных файлов прошли strict verification, бандл запечатал 252 ресурса, flags `0x2` соответствуют ad-hoc подписи. Это конкретное наблюдение before / after, не утверждение о том, какая страница была неверной в прежнем run. Во второй попытке native smoke дополнительно подтвердил успешный запуск и сохранение процесса после снимка.
- После `bootstatus` проверяется не только код выхода. На закреплённой версии Xcode наблюдались `Status=4294967295, isTerminal=YES` / `Finished` и `Status=3, isTerminal=YES` / `Data Migration Failed` при одинаковом коде 0. Скрипт требует единственный терминальный результат в конце вывода и соответствующую строку итога из stdout / stderr. Пустой, неизвестный, неполный или противоречивый ответ останавливает сценарий. Только после первой распознанной ошибки миграции выполняется один shutdown и повторный boot того же созданного UUID, без erase / пересоздания. В `result.json` сохраняются оба `bootAttempts`, даже при успешном повторе; файлы `boot.log`, `boot-retry.log`, `boot-restart.log` не подменяют друг друга. Тайм-аут / ненулевой код команды, ошибка готовности или запуска приложения не приводят к повтору. Это проверяемая попытка восстановления среды, не доказанное решение причины прежнего тайм-аута.
- После упаковки и загрузки результата `scripts/test-ios-simulator.mjs` создаёт отдельный симулятор по типу доступного iPhone с runtime iOS 26.0. Перед create проверяет исполняемый файл `Simulator.app` в выбранном `DEVELOPER_DIR`; при отсутствии переменной использует `xcode-select --print-path`. После create запускает этот файл отдельным дочерним процессом с `-CurrentDeviceUDID` своего UUID, без shell / отсоединения / поиска чужого PID. Отключает запуск последнего устройства, аппаратную клавиатуру и синхронизацию буфера обмена. `simulatorUi` / `simulator-ui.log` сохраняют PID, события и очистку процесса. Изменение подготовлено после `Waiting on BackBoard`; [run 37752572361](https://github.com/Vvvv4a40/NutriFlow/actions/runs/37752572361) подтвердил запуск GUI / успешный boot, но не доказал причинность или работу NutriFlow. Запуск GUI до проверки пригодности устройства используется также в [инфраструктуре WebKit](https://github.com/WebKit/WebKit/blob/main/Tools/Scripts/webkitpy/xcode/simulated_device.py); наш способ прямого запуска выбран ради владения дочерним процессом, глобальная очистка WebKit не переносится.
- Живой GUI не означает готовый iOS. После обязательного успешного boot запускаются стандартные «Настройки» (`com.apple.Preferences`), проверяется возвращённый PID, выполняется ожидание 700 мс и их завершение. Только успех обеих команд отмечается как `readiness.status: "passed"`; ошибка останавливает проверку до установки NutriFlow. Затем install / launch `.app`, три проверки положительного PID / label приложения внутри своего симулятора, `startup.png` между ними и интервалы 10 / 5 секунд. Отдельные проверки жизненного цикла GUI не подменяют PID нашего приложения. Apple-аккаунт не требуется; в текущем эксперименте окно Simulator.app запускается. `simctl` — CLI Xcode для управления симуляторами, `launch` запускает приложение через систему, а `spawn` выполняет команду внутри симулятора. [CLI Apple](https://developer.apple.com/videos/play/wwdc2019/418/).
- Каждая команда имеет предел времени: максимум две попытки boot по 240 секунд, восстановительный shutdown до 15, первый запуск «Настроек» до 120, их завершение прежние 30, install / launch NutriFlow по 60. Готовность с новым лимитом подтверждена run 37755718947; лимит не разрешает retries и не заменяет возвращённый положительный PID. Старт GUI ожидается до 10 секунд; завершение собственного процесса — SIGTERM до 5 секунд, затем при необходимости SIGKILL ещё до 5. PID не берётся из глобального списка процессов. После блокирующих команд скрипт даёт циклу событий обработать события ребёнка; стандартные потоки GUI отключены, чтобы не заблокировать его недренируемыми pipes. Успех отправки сигнала не равен exit. [Правила ChildProcess в Node.js](https://nodejs.org/api/child_process.html).
- Smoke-step ограничен 24 минутами вместо прежних 23: бюджет покрывает дополнительные 90 секунд первого запуска «Настроек» и до 15 секунд проверки состояния при ожидаемом shutdown-ответе, не увеличивая лимит NutriFlow. Тест суммирует максимальные тайм-ауты пути с повторным boot, ожидания GUI / паузы и резерв failure PNG / cleanup. После диагностики закрывается только созданный процесс, затем выключается / удаляется только UUID собственного create. Нет удаления всех симуляторов, erase, загрузки runtime или полного `simctl diagnose`. Ошибки не скрываются; `finally` не гарантируется при принудительной отмене job, облачная VM временная.
- GUI может сам выключить своё устройство при завершении. `shutdownSimulator` не принимает любой status 149: требуются точные SimError 405 / сообщение о состоянии Shutdown, отсутствие ошибки запуска / тайм-аута команды и отдельный успешный `simctl list devices --json` до 15 секунд. В ответе должен быть ровно один собственный UDID со строго Shutdown. Чужой UUID, дубли, другое состояние / формат или ошибка проверки не считаются успехом. `deviceCleanup` сохраняет shutdown / delete, `alreadyShutdown`, проверенное состояние и прежнюю командную ошибку; `cleanup-shutdown.log`, `cleanup-device-state.json` и `cleanup-delete.log` дают первичные данные. Delete только своего UUID выполняется и после сбоя shutdown; cleanup не заменяет основную ошибку. Это идемпотентность: уже достигнутое выключенное состояние не требует повторного выключения, но должно быть доказано.
- `artifact` — сохранённый результат job. `nutriflow-ios-simulator-<run id>-<attempt>` содержит `NutriFlow-ios-simulator-arm64.tar.gz` и `packages.ios.lock.json`; `.app` помещён в tar для сохранения прав. `nutriflow-ios-startup-<run id>-<attempt>` содержит доступные PNG, `result.json`, журнал команд, справку `simctl`, логи boot / GUI / готовности и stdout / stderr приложения. `display-ports.log` перечисляет IO-порты собственного устройства. `app-system.log` ограничен тремя минутами процесса NutriFlow; `launch-system.log` за двадцать минут содержит оба bundle ID / ошибки мигратора. `boot-system.log` ограничен BackBoard / SpringBoard и сообщениями error / failed / watchdog / display, `simulator-host.log` — теми же словами, именем Simulator и PID созданного GUI, без сбора всех процессов хоста. Каждый системный журнал ограничен 30 секундами и 4 МиБ без info / debug; частичные результаты и `diagnostic-errors.log` не скрывают первичный сбой и не гарантируют обнаружения причины. При старте без PID host-журнал не запрашивается. Диагностика загружается после обычного сбоя, если job не отменён. Архив сборки при красном smoke не доказывает запуск. Срок хранения — семь дней; IPA, сертификаты и полный рабочий каталог не публикуются.
- `nutriflow-ios-signature-<run id>-<attempt>` сохраняет отдельные каталоги `ios-signature-before` / `ios-signature-after` на семь дней. Каждый содержит доступные логи и `result.json`; отсутствие after при раннем сбое не является успешной проверкой. Upload выполняется и после обычного сбоя проверки, если job не отменён. Артефакт сборки создаётся только после строгой проверки подписи; startup-артефакт появляется только при дошедшем до smoke сценарии.
- Права workflow ограничены `contents: read`, Git-учётные данные не сохраняются после checkout. Apple-аккаунты / секреты и изменение настроек репозитория не нужны. Job ограничен 36 минутами: добавлен бюджет для двух проверок подписи по 120 секунд и SDK-подписи до двух минут; smoke остался 24. Новый ручной запуск той же ветки отменяет предыдущий.

Репозиторий `Vvvv4a40/NutriFlow` на момент подготовки публичный; стандартные GitHub-hosted runners бесплатны для публичных репозиториев. Это не обещание бесплатной подписи Apple и не относится к larger runners. Для приватных репозиториев нужно учитывать квоты / расходы; видимость репозитория этим шагом не менялась. [Правила GitHub](https://docs.github.com/en/actions/how-tos/write-workflows/choose-where-workflows-run/choose-the-runner-for-a-job).

### Как запустить и прочитать результат

1. Откройте [iOS simulator в GitHub Actions](https://github.com/Vvvv4a40/NutriFlow/actions/workflows/ios-simulator.yml) под своим аккаунтом.
2. Нажмите **Run workflow**, выберите `main`, подтвердите запуск. Файл должен уже находиться в default branch; кнопка требует права записи в репозиторий. [Ручной запуск Actions](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).
3. Дождитесь завершения job. При ошибке откройте первый красный step: исходная подпись, SDK-подпись, строгая проверка и запуск разделены. Успешный upload не подменяет успешную проверку. В `nutriflow-ios-signature-<run id>-<attempt>` сравните before / after: допустимый `invalid` относится только к baseline с `mode: "observe"`, after должен иметь `status: "passed"` / `mode: "require-valid"`.
4. После успеха smoke откройте `startup.png` из `nutriflow-ios-startup-<run id>-<attempt>`: ожидаются заголовок NutriFlow и первый статичный экран, не домашний экран iOS / заставка. Положительный PID и PNG сами по себе не доказывают нужную страницу. `simulatorUi.pid` — оболочка на Mac, `readiness.pid` — «Настройки» в iOS, верхний `pid` — NutriFlow: это три разных процесса. После нормальной очистки GUI статус stopped / cleanup passed; `deviceCleanup` отдельно подтверждает выключение и удаление собственного устройства, включая alreadyShutdown. Неожиданный exit / неподтверждённая очистка не дают зелёный smoke. Фазы select/start-simulator-ui относятся к оболочке; boot / boot-retry — к iOS; readiness-launch/terminate — к «Настройкам». `bootAttempts` сохраняет обе попытки; фаза launch сама по себе не доказывает падение C#-кода.
5. Артефакт сборки сохраняется отдельно. Нужен новый `Run workflow` на актуальном `main`, не `Re-run jobs` старого коммита, чтобы проверить новое содержимое workflow. При обновлении зависимостей отдельно сравните artifact с отслеживаемым lock. Workflow не создаёт коммиты сам. Теперь достаточно ссылки на run: настроенный GitHub CLI позволяет прочитать логи и скачать доступные артефакты без повторной передачи ZIP пользователем.

Для проверенного attempt 2 отчёт startup имеет `status: "passed"`, `phase: "check-after-screenshot"`, одну boot-попытку `Finished`, `readiness.status: "passed"` / PID 9201 и PID NutriFlow 10343. Очистка GUI и `deviceCleanup` также успешна. Проверенный артефакт называется `nutriflow-ios-startup-37761033647-2`; attempt 1 содержит другой результат, их нельзя смешивать. Успех относится к статичному экрану и жизненному циклу симулятора, не к подключению сервера, AI или пользовательским сценариям.

### GitHub CLI на Windows

Установлен GitHub CLI `2.102.0` по пути `C:/Users/kiaev/AppData/Local/Programs/GitHub CLI/bin/gh.exe`; авторизация хранится в Windows keyring. PATH и исходники приложения ради этого не меняются. Не выводите токен, не передавайте его в чат и не используйте `gh auth token` для обычного просмотра результатов. Команды ниже запускаются из корня репозитория; запуск нового run требует разрешения пользователя.

```powershell
$githubCli = 'C:/Users/kiaev/AppData/Local/Programs/GitHub CLI/bin/gh.exe'
& $githubCli workflow run ios-simulator.yml --ref main
& $githubCli run list --workflow ios-simulator.yml --limit 5

$iosRunId = '<run-id>'
& $githubCli run view $iosRunId --log-failed
$iosRunDownload = Join-Path ([System.IO.Path]::GetTempPath()) ('nutriflow-ios-run-' + [guid]::NewGuid().ToString('N'))
& $githubCli run download $iosRunId --dir $iosRunDownload
```

`run download` распаковывает доступные артефакты в новый временный каталог. Это не исходный ZIP: такой результат нельзя выдавать за ZIP-файл с проверенной контрольной суммой. Приложение из архива автоматически не запускается, данные / инструменты ноутбука не очищаются.

### Локальная проверка автоматизации и запуск на Mac

На Windows можно проверить только управление командами на искусственных ответах:

```powershell
node --check scripts/test-ios-simulator.mjs
node --check scripts/verify-ios-simulator-signature.mjs
node --test scripts/tests/ios-simulator*.test.mjs
```

Smoke-тесты проверяют выбор Xcode / путь с пробелами, собственный UUID / GUI PID, события / ранний exit, пределы старта и SIGTERM → SIGKILL, строгий boot / единственный повтор с полной историей, PID / готовность «Настроек» и неизменный лимит NutriFlow. Cleanup-регрессии отличают точную ожидаемую ошибку от остальных кодов / сообщений и тайм-аутов, проверяют LF / CRLF, формат инвентаря, уникальность / состояние собственного UUID, сбои запроса, сохранение первичной ошибки и независимое удаление. Signature-тесты отдельно проверяют инвентарь нативного кода, ARM64 / simulator, неверную подпись, разделение `--observe` / строгого режима, пределы команд и сохранность бандла. Фактическое число / результат текущего запуска фиксируется в [PROGRESS.md](PROGRESS.md), не выводится из количества тестовых файлов. Пакеты npm не устанавливаются; серверный CI подхватывает стандартные Node-тесты существующим wildcard. Это тесты CI-инструментов, не JavaScript в MAUI и не настоящая iOS-проверка.

После успешной сборки выше на Apple Silicon Mac с доступным iOS 26.0 runtime можно выполнить тот же скрипт. Нужны Node.js и исполняемый файл Simulator.app выбранного Xcode. При нескольких Xcode задайте тот же абсолютный `DEVELOPER_DIR` для сборки и smoke; иначе используется активный `xcode-select`. Скрипт требует новый выходной каталог, чтобы не принять старый screenshot за новый результат:

```bash
smokeRoot="$(mktemp -d)"
node scripts/verify-ios-simulator-signature.mjs \
  mobile/NutriFlow.Mobile/bin/Debug/net10.0-ios/iossimulator-arm64/NutriFlow.Mobile.app \
  "$smokeRoot/signature" && \
node scripts/test-ios-simulator.mjs \
  mobile/NutriFlow.Mobile/bin/Debug/net10.0-ios/iossimulator-arm64/NutriFlow.Mobile.app \
  "$smokeRoot/startup"
```

Проверка подписи завершается ненулевым кодом при неверной подписи / платформе или сбое инструментов; `&&` не допускает smoke после такого результата. Smoke отдельно завершается ненулевым кодом при сбое запуска / процесса / снимка / cleanup. Скриншот просматривается отдельно. На текущем Windows эти настоящие проверки невозможны; Apple-инструменты не устанавливались. Они не проверяют API, пользовательские сценарии, минимальную iOS 15, все модели устройств, релизную Apple-подпись или собственный iPhone.

**Архив симулятора нельзя установить на iPhone и нельзя считать IPA для AltStore.** Получение пакета для настоящего `ios-arm64`, проверка AOT / подписи, установка и обновление через выбранный способ — отдельный следующий шаг. Не передавайте Apple-пароль агенту и не сохраняйте сертификаты / пароли в YAML или Git.

## Границы клиента

Мобильный проект не ссылается на `Api`, `Infrastructure` или `Domain`. Сервер остаётся единственным источником расчётов, хранения и AI-интеграций; ключ Groq не переносится на телефон. Следующий шаг после первого запуска — небольшой HTTP-клиент и проверка capabilities по [MOBILE_API.md](MOBILE_API.md), без всех экранов сразу. На телефоне `localhost` означает телефон, не компьютер с API.

Подготовка iOS добавлена по прямому запросу пользователя до завершения Android-сборки. Она ограничена точкой входа, настройками платформы и инструкцией: следующие экраны и API-клиент не реализуются заранее. Общие C# / XAML не отменяют отдельных сборок и проверки каждой платформы. [Требования к платформам MAUI](https://learn.microsoft.com/en-us/dotnet/maui/get-started/installation?view=net-maui-10.0&tabs=visual-studio-code).
