# Мобильный клиент NutriFlow

## Текущее состояние

Выбран нативный .NET MAUI с C# и XAML, сначала Android. Исходники минимального клиента находятся в `mobile/NutriFlow.Mobile`, отдельное решение — `mobile/NutriFlow.Mobile.slnx`. Оно не включено в серверное `NutriFlow.sln`: сборка бэкенда и его CI не должны требовать Android SDK.

Подготовлен один статичный экран, запуск Android, иконка и заставка. Сетевого клиента, дневника, входа, камеры и микрофона здесь пока нет. На 6 октября 2026 года зависимости восстановлены и lock-файл проверен, но сборка остановилась на `XA5300`: отсутствует Android SDK, найденная Java 8 является JRE без инструмента `jar`. APK не получен, экран на устройстве не проверен. Этот мобильный шаг не завершён; подключение API начинается после успешной сборки и первого запуска.

## Что находится в проекте

- `NutriFlow.Mobile.csproj` — `net10.0-android`, `UseMaui`, идентификатор приложения, версия, ресурсы и единственный явный NuGet-пакет `Microsoft.Maui.Controls`. `MauiVersion=10.0.0` согласован с установленной нагрузкой и фиксирует версии MAUI-зависимостей; транзитивный граф записан настоящим restore в `packages.lock.json`.
- `MauiProgram.cs` — создаёт `MauiAppBuilder`, подключает `App` и собирает приложение. Здесь пока нет HTTP-клиента и дополнительных сервисов.
- `App.xaml` / `App.xaml.cs` — общие стили и создание окна с `MainPage`. Используется `CreateWindow`, а не устаревшее присваивание `Application.MainPage`.
- `MainPage.xaml` / `MainPage.xaml.cs` — разметка экрана и связанный C#-класс. Оба файла описывают один `partial`-класс; `InitializeComponent` подключает сгенерированную из XAML часть. На этом шаге в странице нет расчётов и обработчиков ввода.
- `Platforms/Android/MainApplication.cs` — связывает Android-приложение с `MauiProgram`; `MainActivity.cs` — стартовая Android activity с темой заставки.
- `Platforms/Android/AndroidManifest.xml` — настройки Android и ссылки на созданные иконки. Автоматическое резервное копирование приложения отключено; разрешения сети / камеры / микрофона пока не объявлены.
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

## Границы клиента

Мобильный проект не ссылается на `Api`, `Infrastructure` или `Domain`. Сервер остаётся единственным источником расчётов, хранения и AI-интеграций; ключ Groq не переносится на телефон. Следующий шаг после первого запуска — небольшой HTTP-клиент и проверка capabilities по [MOBILE_API.md](MOBILE_API.md), без всех экранов сразу. На телефоне `localhost` означает телефон, не компьютер с API.

iOS пока не указан в `TargetFramework` и не объявляется готовым: добавление платформы, Mac с подходящим Xcode, подпись и запуск на iPhone проверяются отдельно. Общая разметка и C# позволяют продолжить клиент на iOS, но не отменяют требования Apple. [Требования к платформам MAUI](https://learn.microsoft.com/en-us/dotnet/maui/get-started/installation?view=net-maui-10.0&tabs=visual-studio-code).
