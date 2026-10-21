using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private readonly LocalInputDraftStore _inputDrafts;
    private Task _draftSaveTask = Task.CompletedTask;
    private Exception? _draftSaveError;
    private LocalInputDraft? _pendingInputDraft;
    private string? _draftReadError;
    private string _draftSaveMessage = "";
    private int _draftSaveVersion;
    private Editor? _inputEditor;
    private Label? _inputSaveStatus;
    private Button? _inputSaveRetry;

    private bool InputDraftBlocked => _pendingInputDraft is not null || _draftReadError is not null;

    private LocalInputDraft CaptureInputDraft(string? text = null) =>
        new(text ?? _draftText, _session?.MealDate ?? _date, _purpose, _session?.Id);

    private Task QueueInputDraftSave(Guid profileId, LocalInputDraft draft)
    {
        int version = ++_draftSaveVersion;
        SetInputSaveStatus("Сохраняется…");
        _draftSaveTask = PersistInputDraftAsync(profileId, draft, version);
        return _draftSaveTask;
    }

    private async Task PersistInputDraftAsync(Guid profileId, LocalInputDraft draft, int version)
    {
        try
        {
            await _inputDrafts.SaveAsync(profileId, draft);
            if (version == _draftSaveVersion && _profile?.Id == profileId)
            {
                _draftSaveError = null;
                SetInputSaveStatus(string.IsNullOrWhiteSpace(draft.Text) ? "" : "Сохранено на устройстве");
            }
        }
        catch (Exception exception)
        {
            if (version == _draftSaveVersion && _profile?.Id == profileId)
            {
                _draftSaveError = exception;
                SetInputSaveStatus("Не сохранено. Повторите сохранение.");
            }
        }
    }

    private async Task FlushInputDraftAsync()
    {
        Task pending;
        do
        {
            pending = _draftSaveTask;
            await pending;
        } while (!ReferenceEquals(pending, _draftSaveTask));

        if (_draftSaveError is not null)
        {
            throw new InvalidOperationException("Не удалось сохранить набранный текст. Вернитесь к вводу и повторите сохранение.", _draftSaveError);
        }
    }

    private void SetInputSaveStatus(string message)
    {
        _draftSaveMessage = message;
        if (_inputSaveStatus is not null)
        {
            _inputSaveStatus.Text = message;
            _inputSaveStatus.IsVisible = message.Length > 0;
        }
        if (_inputSaveRetry is not null)
        {
            _inputSaveRetry.IsVisible = _draftSaveError is not null && !InputDraftBlocked;
        }
    }

    private async Task LoadInputDraftAsync(Guid profileId)
    {
        _pendingInputDraft = null;
        _draftReadError = null;
        _draftSaveError = null;
        SetInputSaveStatus("");
        try
        {
            LocalInputDraft? draft = await _inputDrafts.ReadAsync(profileId);
            if (draft is null)
            {
                return;
            }

            bool matching = draft.SessionId == _session?.Id && (_session is null ||
                (draft.MealDate == _session.MealDate && draft.Purpose == _purpose));
            bool alreadySubmitted = _session is not null && draft.MatchesLastSubmittedMessage(_session.Messages);
            if (!matching || alreadySubmitted)
            {
                _pendingInputDraft = draft;
                if (alreadySubmitted)
                {
                    _draftReadError = "Сохранённый текст уже есть в сессии. Удалите его копию или явно восстановите текст.";
                }
                return;
            }

            _draftText = draft.Text;
            if (_session is null)
            {
                _date = draft.MealDate;
                _purpose = draft.Purpose;
            }

            SetInputSaveStatus("Сохранено на устройстве");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _draftReadError = "Сохранённый ввод недоступен. Исходный файл не изменён.";
        }
    }

    private View? InputDraftRecovery(Guid profileId)
    {
        if (!InputDraftBlocked)
        {
            return null;
        }

        VerticalStackLayout recovery = new() { Spacing = 10 };
        {
            recovery.Children.Add(Ui.Text(_draftReadError ?? "Остался неотправленный текст другой записи."));
            if (_pendingInputDraft is not null)
            {
                Button restore = Ui.Action("Восстановить текст", () => RunAsync(async () =>
                {
                    LocalInputDraft draft = _pendingInputDraft!;
                    MealSessionResponse? previous = draft.SessionId is Guid id
                        ? await _client.FindSessionAsync(profileId, id) : null;
                    bool canResume = previous is not null && previous.Status != "Confirmed";
                    if (canResume && draft.MatchesLastSubmittedMessage(previous!.Messages) &&
                        !await DisplayAlertAsync("Вернуть уже отправленный текст?", "Это уточнение уже добавлено в сессию. Восстановление позволит отправить его ещё раз.", "Вернуть", "Отмена"))
                    {
                        return;
                    }
                    if (_session is not null && _session.Id != previous?.Id &&
                        !await DisplayAlertAsync("Восстановить текст?", "Текущий расчёт будет заменён на экране. Записи дневника не изменятся.", "Восстановить", "Отмена"))
                    {
                        return;
                    }

                    if (draft.SessionId is not null && !canResume &&
                        !await DisplayAlertAsync("Открыть как новое описание?", "Исходная сессия завершена или недоступна. Текст не будет добавлен к другому расчёту.", "Открыть", "Отмена"))
                    {
                        return;
                    }

                    MealSessionPurpose purpose = canResume
                        ? previous!.Purpose == "CreateDish" ? MealSessionPurpose.CreateDish : MealSessionPurpose.Diary
                        : draft.Purpose;
                    LocalInputDraft restored = new(draft.Text, canResume ? previous!.MealDate : draft.MealDate,
                        purpose, canResume ? previous!.Id : null);
                    await QueueInputDraftSave(profileId, restored);
                    await FlushInputDraftAsync();
                    _session = canResume ? previous : null;
                    _date = restored.MealDate;
                    _purpose = restored.Purpose;
                    _draftText = restored.Text;
                    if (_session is null)
                    {
                        ProfileSettings.ClearSession(profileId);
                    }
                    else
                    {
                        ProfileSettings.SaveSession(profileId, _session.Id);
                    }

                    _pendingInputDraft = null;
                    await RenderAsync();
                }));
                restore.AutomationId = "input-draft-restore";
                recovery.Children.Add(restore);
            }
            else
            {
                Button retry = Ui.Action("Повторить чтение", () => RunAsync(async () =>
                {
                    await LoadInputDraftAsync(profileId);
                    await RenderAsync();
                }), true);
                retry.AutomationId = "input-draft-reload";
                recovery.Children.Add(retry);
            }

            Button discard = Ui.Action("Удалить сохранённый текст", () => RunAsync(async () =>
            {
                if (!await DisplayAlertAsync("Удалить сохранённый ввод?", "Будет удалён только неотправленный текст этого профиля. Дневник, продукты и блюда останутся.", "Удалить", "Отмена"))
                {
                    return;
                }

                await _inputDrafts.DiscardAsync(profileId);
                ++_draftSaveVersion;
                _pendingInputDraft = null;
                _draftReadError = null;
                _draftSaveError = null;
                _draftText = "";
                SetInputSaveStatus("");
                await RenderAsync();
            }), true);
            discard.AutomationId = "input-draft-discard";
            recovery.Children.Add(discard);
        }

        return Ui.Card(recovery);
    }

    private async Task ClearSubmittedInputAsync(Guid profileId)
    {
        if (_profile?.Id == profileId)
        {
            _inputEditor = null;
            _draftText = "";
            _pendingInputDraft = null;
            _draftReadError = null;
            ++_draftSaveVersion;
        }

        try
        {
            await _inputDrafts.ClearAsync(profileId);
            if (_profile?.Id == profileId)
            {
                _draftSaveError = null;
                SetInputSaveStatus("");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (_profile?.Id == profileId)
            {
                _draftReadError = "Расчёт сохранён, но не удалось убрать отправленный текст. Проверьте восстановление ввода.";
            }
        }
    }

    private async Task SetSessionAsync(Guid profileId, MealSessionResponse session)
    {
        SetSession(profileId, session);
        await ClearSubmittedInputAsync(profileId);
    }

    private Task<bool> CanDiscardInputAsync(string title, string message) =>
        InputDraftBlocked ? Task.FromResult(false) : string.IsNullOrWhiteSpace(_draftText)
            ? Task.FromResult(true) : DisplayAlertAsync(title, message, "Продолжить", "Отмена");
}
