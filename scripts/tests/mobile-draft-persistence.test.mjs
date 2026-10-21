import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const source = name => readFileSync(new URL(name, mobile), "utf8");
const drafts = source("MainPage.Drafts.cs");
const input = source("MainPage.Input.cs");
const page = source("MainPage.xaml.cs");

test("unsubmitted input stores each current editor snapshot without a debounce or network request", () => {
    const changed = input.split("editor.TextChanged +=")[1].split("VerticalStackLayout alternatives")[0];
    assert.match(changed, /ReferenceEquals\(_inputEditor, editor\)/);
    assert.match(changed, /_profile\?\.Id != profileId/);
    assert.match(changed, /_session\?\.Id != inputSessionId/);
    assert.match(changed, /InputDraftBlocked/);
    assert.match(changed, /_draftText = editor\.Text \?\? ""/);
    assert.match(changed, /QueueInputDraftSave\(profileId, CaptureInputDraft\(\)\)/);
    assert.doesNotMatch(changed, /Task\.Delay|Timer|CreateSessionAsync|AddMessageAsync/);
    assert.match(page, /_inputDrafts = new LocalInputDraftStore\(profiles\)/);
    assert.match(drafts, /_draftSaveTask = PersistInputDraftAsync\(profileId, draft, version\)/);
    assert.match(drafts, /ReferenceEquals\(pending, _draftSaveTask\)/);
    assert.match(drafts, /if \(_draftSaveError is not null\)[\s\S]*throw new InvalidOperationException/);
});

test("saving has visible status and retry while invalid stored input cannot abort the root screen", () => {
    assert.match(input, /AutomationId = "input-save-status"/);
    assert.match(input, /AutomationId = "input-draft-retry"/);
    assert.match(drafts, /Сохраняется…/);
    assert.match(drafts, /Сохранено на устройстве/);
    assert.match(drafts, /Не сохранено/);
    const load = drafts.split("private async Task LoadInputDraftAsync")[1].split("private View? InputDraftRecovery")[0];
    assert.match(load, /catch \(Exception exception\) when \(exception is IOException or InvalidDataException or UnauthorizedAccessException\)/);
    assert.match(load, /_draftReadError =/);
    assert.doesNotMatch(load, /ClearAsync|DiscardAsync|File\.Delete/);
});

test("draft restore checks session, date and purpose and requires explicit recovery on mismatch", () => {
    assert.match(drafts, /draft\.SessionId == _session\?\.Id/);
    assert.match(drafts, /draft\.MealDate == _session\.MealDate && draft\.Purpose == _purpose/);
    assert.match(drafts, /if \(!matching \|\| alreadySubmitted\)\s*\{\s*_pendingInputDraft = draft;/);
    assert.match(drafts, /input-draft-restore/);
    assert.match(drafts, /FindSessionAsync\(profileId, id\)/);
    assert.match(drafts, /previous\.Status != "Confirmed"/);
    assert.match(drafts, /Открыть как новое описание/);
    assert.match(input, /editor\.IsEnabled = !InputDraftBlocked/);
    const discard = drafts.split("Button discard =")[1].split("return Ui.Card(recovery)")[0];
    assert.match(discard, /await DisplayAlertAsync\("Удалить сохранённый ввод\?/);
    assert.match(discard, /await _inputDrafts\.DiscardAsync\(profileId\)/);
});

test("text already submitted before a cleanup error cannot silently return as new input", () => {
    const load = drafts.split("private async Task LoadInputDraftAsync")[1].split("private View? InputDraftRecovery")[0];
    assert.match(load, /draft\.MatchesLastSubmittedMessage\(_session\.Messages\)/);
    assert.match(load, /if \(!matching \|\| alreadySubmitted\)/);
    assert.match(load, /_pendingInputDraft = draft/);
    assert.match(drafts, /draft\.MatchesLastSubmittedMessage\(previous!\.Messages\)/);
    assert.match(drafts, /DisplayAlertAsync\("Вернуть уже отправленный текст\?/);
});

test("profile changes flush durable text before switching and restore the new profile without a discard prompt", () => {
    const navigation = source("MainPage.Navigation.cs");
    const switching = navigation.split("int selectedIndex =")[1].split("protected override bool OnBackButtonPressed")[0];
    assert.match(switching, /await FlushInputDraftAsync\(\);[\s\S]*_profile = await _profiles\.SelectAsync/);
    assert.match(switching, /await ResumeSessionAsync\(_profile\.Id\);\s*await LoadInputDraftAsync\(_profile\.Id\)/);
    assert.doesNotMatch(navigation, /CanLeaveDraftTextAsync|Набранный, но ещё не отправленный текст будет очищен/);
    assert.match(page, /await ResumeSessionAsync\(selected\.Id\);\s*await LoadInputDraftAsync\(selected\.Id\)/);
    assert.match(page, /_inputEditor = null;\s*_inputSaveStatus = null;\s*_inputSaveRetry = null;/);
});

test("AI submission flushes first and only clears text after a successful parsed session", () => {
    const submit = input.split("Button submit =")[1].split("submit.AutomationId")[0];
    assert.match(submit, /await FlushInputDraftAsync\(\);[\s\S]*await _client\.CreateSessionAsync/);
    assert.match(submit, /await _client\.AddMessageAsync/);
    assert.match(submit, /await SetSessionAsync\(profileId, session\)/);
    assert.doesNotMatch(submit, /finally|_draftText = ""|DiscardAsync/);
    const set = drafts.split("private async Task SetSessionAsync")[1].split("private Task<bool>")[0];
    assert.match(set, /SetSession\(profileId, session\);\s*await ClearSubmittedInputAsync\(profileId\)/);
});

test("purpose and new capture date changes persist without retargeting an existing clarification", () => {
    assert.match(source("MainPage.InputLayout.cs"), /_purpose = purpose;\s*_ = QueueInputDraftSave\(CurrentProfileId\(\), CaptureInputDraft\(\)\)/);
    assert.match(drafts, /_session\?\.MealDate \?\? _date, _purpose, _session\?\.Id/);
    const diary = source("MainPage.Diary.cs");
    assert.match(diary, /!InputDraftBlocked && !string\.IsNullOrWhiteSpace\(_draftText\) && _session is null/);
    assert.match(diary, /CaptureInputDraft\(\) with \{ MealDate = date \}/);
});

test("confirmation warns about unsent text before writing and reports successful result after cleanup trouble", () => {
    const preview = source("MainPage.Preview.cs");
    const confirm = preview.split("Button confirm =")[1].split("confirm.AutomationId")[0];
    assert.match(confirm, /await FlushInputDraftAsync\(\);[\s\S]*CanDiscardInputAsync[\s\S]*await _client\.ConfirmAsync/);
    assert.match(confirm, /if \(result\.Session\.Status == "Confirmed"\)[\s\S]*await ClearSubmittedInputAsync\(profileId\)/);
    assert.match(confirm, /if \(_draftReadError is not null\)[\s\S]*message \+=/);
    assert.match(confirm, /DisplayAlertAsync\("Сохранено", message/);
});

test("backup and all manually created calculation paths use the durable input workflow", () => {
    assert.match(source("MainPage.Backups.cs"), /ExportBackupAsync\(Guid profileId\) => RunAsync\(async \(\) =>\s*\{\s*await FlushInputDraftAsync\(\)/);
    for (const filename of ["MainPage.Products.cs", "MainPage.Dishes.cs"]) {
        const content = source(filename);
        assert.match(content, /await SetSessionAsync\(profileId, await _client\.Create/);
        assert.doesNotMatch(content, /(?<!Async)\bSetSession\(profileId,/);
    }
});
