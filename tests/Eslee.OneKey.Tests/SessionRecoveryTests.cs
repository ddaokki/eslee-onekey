using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

public sealed class SessionRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onekey-recovery-tests", Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task PreparationCanBeRestoredAfterRestartAndRemovedOnSuccess()
    {
        Directory.CreateDirectory(root);
        var paths = new ApplicationPaths(Path.Combine(root, "store"));
        var profile = new GameAccountProfile { SessionFilePath = Path.Combine(root, "session.yaml") };
        GameAccountSessionService Create() => new(paths, new DpapiSecretStore(paths), new FakeProcessService(), new FakeLogger(), TimeSpan.Zero);
        var original = GameAccountSessionTests.Session("A", "token");
        await File.WriteAllTextAsync(profile.SessionFilePath, original);
        await Create().CaptureAsync(profile, default);
        await Create().PrepareForNewSignInAsync(profile, default);
        Assert.True((await Create().RestorePreparedSessionAsync(profile, default)).CanContinue);
        Assert.Equal(original, await File.ReadAllTextAsync(profile.SessionFilePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.Root, "session-recovery")));
        await Create().PrepareForNewSignInAsync(profile, default);
        await File.WriteAllTextAsync(profile.SessionFilePath, GameAccountSessionTests.Session("B", "token"));
        await Create().CaptureAsync(profile with { Id = Guid.NewGuid() }, default);
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.Root, "session-recovery")));
    }

    [Fact]
    public async Task UnknownCandidateSurvivesRestartAndRequiresExplicitCapture()
    {
        Directory.CreateDirectory(root);
        var paths = new ApplicationPaths(Path.Combine(root, "store"));
        var profile = new GameAccountProfile { SessionFilePath = Path.Combine(root, "session.yaml") };
        GameAccountSessionService Create() => new(paths, new DpapiSecretStore(paths), new FakeProcessService(), new FakeLogger(), TimeSpan.Zero);
        await File.WriteAllTextAsync(profile.SessionFilePath, "refresh_token: A");
        await Create().CaptureAsync(profile, default);
        await File.WriteAllTextAsync(profile.SessionFilePath, "refresh_token: rotated-unknown");
        Assert.Equal(GameSessionOutcome.Unknown, (await Create().ActivateAsync(profile, default)).Outcome);
        File.Delete(profile.SessionFilePath);
        await Create().RestoreLatestCandidateAsync(profile, default);
        Assert.Equal("refresh_token: rotated-unknown", await File.ReadAllTextAsync(profile.SessionFilePath));
        Assert.Equal("refresh_token: A", await new DpapiSecretStore(paths).LoadAccountSessionAsync(profile.Id, default));
        await Create().CaptureAsync(profile, default);
        Assert.Equal("refresh_token: rotated-unknown", await new DpapiSecretStore(paths).LoadAccountSessionAsync(profile.Id, default));
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.Root, "session-recovery")));
    }
    [Fact]
    public async Task DestinationReplaceFailureLeavesCompleteOriginalAndNoTemporaryPlaintext()
    {
        Directory.CreateDirectory(root);
        var paths = new ApplicationPaths(Path.Combine(root, "store"));
        var profile = new GameAccountProfile { SessionFilePath = Path.Combine(root, "session.yaml") };
        var service = new GameAccountSessionService(paths, new DpapiSecretStore(paths), new FakeProcessService(), new FakeLogger(), TimeSpan.Zero);
        await File.WriteAllTextAsync(profile.SessionFilePath, "original");
        await service.CaptureAsync(profile, default);
        await File.WriteAllTextAsync(profile.SessionFilePath, "current");
        using (File.Open(profile.SessionFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(GameSessionOutcome.Failed, (await service.ActivateAsync(profile, default)).Outcome);
            Assert.Equal("current", await File.ReadAllTextAsync(profile.SessionFilePath));
        }
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.True((await service.RestorePreparedSessionAsync(profile, default)).CanContinue);
        Assert.Equal("current", await File.ReadAllTextAsync(profile.SessionFilePath));
    }

    [Fact]
    public async Task LegacyGlobalMarkerCannotAuthorizeOpaqueRotationAfterUpgrade()
    {
        Directory.CreateDirectory(root);
        var paths = new ApplicationPaths(Path.Combine(root, "store")); paths.EnsureDirectories();
        var profile = new GameAccountProfile { SessionFilePath = Path.Combine(root, "session.yaml") };
        var secrets = new DpapiSecretStore(paths);
        await secrets.SaveAccountSessionAsync(profile.Id, "refresh_token: old", default);
        await File.WriteAllTextAsync(Path.Combine(paths.Root, "active-account-profile.json"),
            System.Text.Json.JsonSerializer.Serialize(new { ProfileId = profile.Id, Fingerprint = "legacy" }));
        await File.WriteAllTextAsync(profile.SessionFilePath, "refresh_token: rotated");
        var service = new GameAccountSessionService(paths, secrets, new FakeProcessService(), new FakeLogger(), TimeSpan.Zero);
        Assert.Equal(GameSessionOutcome.Unknown, (await service.ActivateAsync(profile, default)).Outcome);
        Assert.Equal("refresh_token: rotated", await File.ReadAllTextAsync(profile.SessionFilePath));
        Assert.Equal("refresh_token: old", await secrets.LoadAccountSessionAsync(profile.Id, default));
        File.Delete(profile.SessionFilePath);
        Assert.True((await service.RestoreLatestCandidateAsync(profile, default)).CanContinue);
        Assert.Equal("refresh_token: rotated", await File.ReadAllTextAsync(profile.SessionFilePath));
    }

    [Fact]
    public async Task LegacyPlaintextAsideMigratesToRecoverableEncryptedCandidate()
    {
        Directory.CreateDirectory(root);
        var paths = new ApplicationPaths(Path.Combine(root, "store"));
        var profile = new GameAccountProfile { SessionFilePath = Path.Combine(root, "session.yaml") };
        await File.WriteAllTextAsync(profile.SessionFilePath + ".onekey-aside", "legacy-secret");
        var service = new GameAccountSessionService(paths, new DpapiSecretStore(paths), new FakeProcessService(), new FakeLogger(), TimeSpan.Zero);
        Assert.True((await service.RestoreLatestCandidateAsync(profile, default)).CanContinue);
        Assert.False(File.Exists(profile.SessionFilePath + ".onekey-aside"));
        Assert.Equal("legacy-secret", await File.ReadAllTextAsync(profile.SessionFilePath));
        Assert.All(Directory.GetFiles(paths.Root, "*.dat", SearchOption.AllDirectories),
            file => Assert.DoesNotContain("legacy-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file))));
        await service.CaptureAsync(profile, default);
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.Root, "session-recovery")));
    }

    [Fact]
    public async Task LogoutWithoutMarkerCannotLoseCredentialsFlushedDuringShutdown()
    {
        Directory.CreateDirectory(root);
        var paths = new ApplicationPaths(Path.Combine(root, "store"));
        var profile = new GameAccountProfile { SessionFilePath = Path.Combine(root, "session.yaml"), LauncherProcessNames = ["fake"] };
        var secrets = new DpapiSecretStore(paths);
        await secrets.SaveAccountSessionAsync(profile.Id, "refresh_token: stored", default);
        var process = new FlushingProcesses(() => File.WriteAllText(profile.SessionFilePath, "refresh_token: flushed"));
        var service = new GameAccountSessionService(paths, secrets, process, new FakeLogger(), TimeSpan.Zero);
        Assert.Equal(GameSessionOutcome.Unknown, (await service.ActivateAsync(profile, default)).Outcome);
        Assert.Equal("refresh_token: flushed", await File.ReadAllTextAsync(profile.SessionFilePath));
        Assert.Equal("refresh_token: stored", await secrets.LoadAccountSessionAsync(profile.Id, default));
    }

    [Fact]
    public async Task SeparateServiceInstancesSerializePrepareAndCapture()
    {
        Directory.CreateDirectory(root);
        var paths = new ApplicationPaths(Path.Combine(root, "store"));
        var profile = new GameAccountProfile { SessionFilePath = Path.Combine(root, "session.yaml"), LauncherProcessNames = ["fake"] };
        await File.WriteAllTextAsync(profile.SessionFilePath, "original");
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FlushingProcesses(() => { }, stopping, resume);
        var first = new GameAccountSessionService(paths, new DpapiSecretStore(paths), process, new FakeLogger(), TimeSpan.Zero);
        var second = new GameAccountSessionService(paths, new DpapiSecretStore(paths), new FakeProcessService(), new FakeLogger(), TimeSpan.Zero);
        var prepare = first.PrepareForNewSignInAsync(profile, default);
        await stopping.Task;
        var capture = second.CaptureAsync(profile, default);
        Assert.False(capture.IsCompleted);
        resume.SetResult();
        Assert.True((await prepare).CanContinue);
        Assert.False(await capture); // observes the completed preparation, never its transient state
        Assert.True((await second.RestorePreparedSessionAsync(profile, default)).CanContinue);
        Assert.Equal("original", await File.ReadAllTextAsync(profile.SessionFilePath));
    }

    private sealed class FlushingProcesses(Action flush, TaskCompletionSource? stopping = null, TaskCompletionSource? resume = null) : IProcessService
    {
        private bool running = true;
        public Task<bool> IsRunningAsync(string name, CancellationToken cancellationToken) => Task.FromResult(running);
        public async Task StopAsync(string name, CancellationToken cancellationToken)
        {
            stopping?.SetResult();
            if (resume is not null) await resume.Task;
            flush(); running = false;
        }
        public Task StartAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> BringToFrontAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
