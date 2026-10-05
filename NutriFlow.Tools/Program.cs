using NutriFlow.Infrastructure.Backups;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments is ["--help"])
    {
        PrintUsage(Console.Out);
        return 0;
    }

    if (!TryParseArguments(arguments, out var command, out var error))
    {
        Console.Error.WriteLine(error);
        PrintUsage(Console.Error);
        return 2;
    }

    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        if (command.Name == "backup")
        {
            var manifest = await DataBackup.CreateAsync(
                command.Options["--database"],
                command.Options["--photos"],
                command.Options["--output"],
                cancellation.Token);
            Console.WriteLine($"Резервная копия создана: {Path.GetFullPath(command.Options["--output"])}");
            Console.WriteLine($"Проверено файлов: {manifest.Files.Count}");
        }
        else if (command.Name == "verify")
        {
            var manifest = await DataBackup.VerifyAsync(command.Options["--backup"], cancellation.Token);
            Console.WriteLine($"Резервная копия проверена: {Path.GetFullPath(command.Options["--backup"])}");
            Console.WriteLine($"Проверено файлов: {manifest.Files.Count}");
        }
        else
        {
            var manifest = await DataBackup.RestoreAsync(
                command.Options["--backup"],
                command.Options["--output"],
                cancellation.Token);
            Console.WriteLine($"Резервная копия восстановлена: {Path.GetFullPath(command.Options["--output"])}");
            Console.WriteLine($"Восстановлено и проверено файлов: {manifest.Files.Count}");
            Console.WriteLine("API и миграции не запускались. Исходные данные не изменены.");
            Console.WriteLine("Для использования восстановленных данных явно укажите пути к базе и фотографиям в настройках API.");
        }

        return 0;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("Операция отменена.");
        return 130;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Ошибка: {exception.Message}");
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static bool TryParseArguments(string[] arguments, out ParsedCommand command, out string error)
{
    command = new ParsedCommand(string.Empty, new Dictionary<string, string>(StringComparer.Ordinal));
    error = string.Empty;

    if (arguments.Length == 0 || arguments[0] is not ("backup" or "verify" or "restore"))
    {
        error = "Укажите команду backup, verify или restore.";
        return false;
    }

    var name = arguments[0];
    string[] requiredOptions = name switch
    {
        "backup" => ["--database", "--photos", "--output"],
        "verify" => ["--backup"],
        _ => ["--backup", "--output"]
    };
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    var offline = false;

    for (var index = 1; index < arguments.Length; index++)
    {
        var option = arguments[index];
        if (name == "backup" && option == "--offline")
        {
            if (offline)
            {
                error = "Параметр --offline указан повторно.";
                return false;
            }

            offline = true;
            continue;
        }

        if (!requiredOptions.Contains(option, StringComparer.Ordinal))
        {
            error = $"Неизвестный параметр: {option}.";
            return false;
        }

        if (options.ContainsKey(option))
        {
            error = $"Параметр {option} указан повторно.";
            return false;
        }

        if (index + 1 >= arguments.Length
            || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            error = $"Для параметра {option} нужен путь.";
            return false;
        }

        options.Add(option, arguments[++index]);
    }

    var missingOptions = requiredOptions.Where(option => !options.ContainsKey(option)).ToArray();
    if (missingOptions.Length > 0)
    {
        error = $"Не указаны обязательные параметры: {string.Join(", ", missingOptions)}.";
        return false;
    }

    if (name == "backup" && !offline)
    {
        error = "Остановите API и все процессы записи, затем подтвердите это параметром --offline.";
        return false;
    }

    command = new ParsedCommand(name, options);
    return true;
}

static void PrintUsage(TextWriter writer)
{
    writer.WriteLine("backup --database <путь> --photos <путь> --output <новая-папка> --offline");
    writer.WriteLine("verify --backup <папка>");
    writer.WriteLine("restore --backup <папка> --output <новая-несуществующая-папка>");
    writer.WriteLine("--help");
    writer.WriteLine("Для backup параметр --offline подтверждает, что API и все процессы записи остановлены.");
    writer.WriteLine("Restore не запускает API и миграции; пути к восстановленным данным настраиваются явно.");
}

sealed record ParsedCommand(string Name, Dictionary<string, string> Options);
