import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const backups = readFileSync(new URL("MainPage.Backups.cs", mobile), "utf8");
const settings = readFileSync(new URL("MainPage.Settings.cs", mobile), "utf8");
const navigation = readFileSync(new URL("MainPage.Navigation.cs", mobile), "utf8");
const files = readFileSync(new URL("BackupFileAccess.cs", mobile), "utf8");
const activity = readFileSync(new URL("Platforms/Android/MainActivity.cs", mobile), "utf8");

test("backup actions are available in settings and restoration before creating a profile", () => {
    for (const id of ["settings-backup-export", "settings-backup-restore"]) {
        assert.ok(settings.includes(`"${id}"`));
    }
    assert.ok(navigation.includes('"welcome-backup-restore"'));
    assert.match(settings, /Архив не зашифрован/);
    assert.match(settings, /не ключ Groq/);
});

test("export passes the selected profile and active session without accessing its secret", () => {
    assert.match(backups, /ExportAsync\(profileId, path, ProfileSettings\.SessionId\(profileId\)\)/);
    assert.doesNotMatch(backups, /GetGroqAsync|SecureStorage|GetDatabasePath/);
    assert.match(backups, /if \(await BackupFileAccess\.SaveAsync/);
    assert.match(backups, /finally[\s\S]*File\.Delete\(path\)/);
});

test("restoration reads a picked stream and selects only the successfully created profile", () => {
    assert.match(backups, /FilePicker\.Default\.PickAsync/);
    assert.match(backups, /if \(file is null\)[\s\S]*return;/);
    assert.match(backups, /await using Stream stream = await file\.OpenReadAsync\(\)/);
    assert.doesNotMatch(backups, /file\.FullPath|File\.Delete\(file/);
    assert.match(backups, /ImportAsync\(stream, profileName\)/);
    assert.ok(backups.indexOf("ImportAsync(stream, profileName)") < backups.indexOf("_profiles.SelectAsync(result.Profile.Id)"));
    assert.match(backups, /ProfileSettings\.SaveSession\(result\.Profile\.Id, sessionId\)/);
});

test("Android backup saving uses a document result rather than treating sharing as persistence", () => {
    assert.match(activity, /RegisterForActivityResult/);
    assert.match(activity, /ActivityResultContracts\.CreateDocument\("application\/zip"\)/);
    assert.match(activity, /OnDestroy\(\)[\s\S]*TrySetCanceled/);
    assert.match(files, /destination is null[\s\S]*return false/);
    assert.match(files, /CopyToAsync\(output\)/);
    assert.match(files, /FlushAsync\(\)/);
    assert.doesNotMatch(files, /Share\.Default|ShareFileRequest/);
});
