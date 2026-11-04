using Bit.Bsqlite.Interop;
using NutriFlow.Web.Services;

namespace NutriFlow.Web.Tests;

// the parts of the browser backup that run without bsqlite; restoring and saving copies is checked in the preview
// and, with the same seam on files, by LocalProfileBackupHostTests of the mobile core
public sealed class WebBackupDatabasesTests : IDisposable
{
    private const string Binding = "nutriflow.bsqlite-binding.";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-web-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeJsRuntime _js = new();
    private readonly NoSessions _sessions = new();

    [Fact]
    public async Task FileThatIsNotSqliteIsRejectedBeforeItReachesTheBrowserStorage()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "nutriflow.db");
        await File.WriteAllBytesAsync(path, new byte[4096]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create().UseAsync(path, writable: true, (_, _) => Task.FromResult(true), CancellationToken.None));

        Assert.Empty(_js.Storage.Databases);
        Assert.Equal(0, _sessions.Opened);
    }

    [Fact]
    public async Task RestoredDatabaseMovesToWhereTheProfileOpensItAndLeavesTheMirroredFolder()
    {
        Guid profileId = Guid.NewGuid();
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Combine(_root, "nutriflow.db"), [1, 2, 3]);
        WebBackupDatabases databases = Create();

        await databases.InstallRestoredAsync(profileId, _root, CancellationToken.None);

        Assert.Equal([1, 2, 3], _js.Storage.Databases[$"nutriflow/profile-{profileId:N}.db"]);
        Assert.False(File.Exists(Path.Combine(_root, "nutriflow.db")));

        _js.LocalStorage[$"{Binding}nutriflow/profile-{profileId:N}.db"] = "opfs";
        await databases.DiscardRestoredAsync(profileId, CancellationToken.None);

        Assert.Empty(_js.Storage.Databases);
        Assert.Empty(_js.LocalStorage);
    }

    [Fact]
    public async Task CopiesLeftByAClosedPageAreRemovedOnceTheFileIsFree()
    {
        _js.Storage.Databases["nutriflow/backup-0123456789abcdef0123456789abcdef.db"] = [1];
        _js.Storage.Databases["nutriflow/profile-0123456789abcdef0123456789abcdef.db"] = [2];
        _js.Storage.Files["nutriflow/files/profiles.json"] = [3];
        _js.LocalStorage[$"{Binding}nutriflow/backup-0123456789abcdef0123456789abcdef.db"] = "opfs";
        _js.LocalStorage[$"{Binding}nutriflow/profile-0123456789abcdef0123456789abcdef.db"] = "opfs";
        _js.Storage.LockedDatabaseRemovals = 1;

        await Create().RemoveLeftoversAsync();

        Assert.Equal(["nutriflow/backup-0123456789abcdef0123456789abcdef.db"], _js.Storage.DatabaseRemovals);
        Assert.Equal(["nutriflow/profile-0123456789abcdef0123456789abcdef.db"], _js.Storage.Databases.Keys);
        Assert.Equal([$"{Binding}nutriflow/profile-0123456789abcdef0123456789abcdef.db"], _js.LocalStorage.Keys);
        Assert.Single(_js.Storage.Files);
    }

    private WebBackupDatabases Create() =>
        new(_sessions, new BsqliteBindingStore(_js), new WebProfileDatabase(_sessions), new OpfsFiles(_js));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoSessions : IBsqliteSessionFactory
    {
        public int Opened { get; private set; }

        public Task<BsqliteSession> OpenAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            Opened++;
            throw new InvalidOperationException("Bsqlite runs only in the browser.");
        }
    }
}
