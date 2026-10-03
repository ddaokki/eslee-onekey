using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "onekey-audit-tests", Guid.NewGuid().ToString("N"));
    private readonly CancellationToken ct = CancellationToken.None;
    private string Live => Path.Combine(root, "launcher", "session.yaml");
    private ApplicationPaths Paths => new(Path.Combine(root, "store"));
    private GameAccountProfile Profile => new() { SessionFilePath = Live, LauncherProcessNames = ["fake"] };
    private GameAccountSessionService Service(IProcessService? processes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Live)!);
        return new(Paths, new DpapiSecretStore(Paths), processes ?? new FakeProcessService(), new FakeLogger(), TimeSpan.Zero);
    }

    [Fact]
    public async Task ManualAccountChangeDoesNotOverwriteEnrolledAccount()
    {
        var service = Service(); var profile = Profile;
        var original = Session("A", "old");
        await File.WriteAllTextAsync(Live, original);
        await service.CaptureAsync(profile, ct);
        await File.WriteAllTextAsync(Live, Session("B", "new"));
        var result = await service.ActivateAsync(profile, ct);
        Assert.NotEqual(GameSessionOutcome.AlreadyActive, result.Outcome);
        Assert.Equal(original, await new DpapiSecretStore(Paths).LoadAccountSessionAsync(profile.Id, ct));
    }

    [Fact]
    public async Task OpaqueRotationDoesNotSilentlyOverwriteKnownAccount()
    {
        var service = Service(); var profile = Profile;
        await File.WriteAllTextAsync(Live, "refresh_token: opaque-A");
        await service.CaptureAsync(profile, ct);
        await File.WriteAllTextAsync(Live, "refresh_token: opaque-B");
        var result = await service.ActivateAsync(profile, ct);
        Assert.False(result.CanContinue);
        Assert.Equal("refresh_token: opaque-A", await new DpapiSecretStore(Paths).LoadAccountSessionAsync(profile.Id, ct));
    }

    [Fact]
    public async Task PreparationLeavesNoPlaintextAside()
    {
        var service = Service(); var profile = Profile;
        await File.WriteAllTextAsync(Live, "refresh_token: fake-secret");
        await service.CaptureAsync(profile, ct);
        Assert.True((await service.PrepareForNewSignInAsync(profile, ct)).CanContinue);
        Assert.False(File.Exists(Live + ".onekey-aside"));
        Assert.All(Directory.EnumerateFiles(Paths.Root, "*", SearchOption.AllDirectories),
            file => Assert.DoesNotContain("fake-secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedLauncherStopPreservesLiveAndMarkers(bool denied)
    {
        var processes = new ResistantProcesses(denied);
        var service = Service(processes); var first = Profile; var second = Profile;
        await File.WriteAllTextAsync(Live, "first"); await service.CaptureAsync(first, ct);
        await File.WriteAllTextAsync(Live, "second"); await service.CaptureAsync(second, ct);
        var markers = Directory.GetFiles(Paths.Root, "*.json").ToDictionary(f => f, File.ReadAllBytes);
        var result = await service.ActivateAsync(first, ct);
        Assert.Equal(GameSessionOutcome.Failed, result.Outcome);
        Assert.Equal("second", await File.ReadAllTextAsync(Live));
        foreach (var entry in markers) Assert.Equal(entry.Value, await File.ReadAllBytesAsync(entry.Key));
    }

    [Fact]
    public async Task DifferentSessionPathsCannotRecaptureEachOthersAccounts()
    {
        var service = Service(); var first = Profile;
        await File.WriteAllTextAsync(Live, "refresh_token: A"); await service.CaptureAsync(first, ct);
        var second = Profile with { SessionFilePath = Path.Combine(root, "other.yaml") };
        await File.WriteAllTextAsync(second.SessionFilePath, "refresh_token: B");
        await service.PrepareForNewSignInAsync(second, ct);
        Assert.Equal("refresh_token: A", await new DpapiSecretStore(Paths).LoadAccountSessionAsync(first.Id, ct));
    }

    [Fact]
    public async Task MarkerCommitFailurePreservesOriginalSession()
    {
        var service = Service(); var first = Profile; var second = Profile;
        await File.WriteAllTextAsync(Live, "first"); await service.CaptureAsync(first, ct);
        await File.WriteAllTextAsync(Live, "second"); await service.CaptureAsync(second, ct);
        var marker = Directory.GetFiles(Paths.Root, "*.json").Single();
        var before = await File.ReadAllBytesAsync(marker);
        using (File.Open(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(GameSessionOutcome.Failed, (await service.ActivateAsync(first, ct)).Outcome);
            Assert.Equal("second", await File.ReadAllTextAsync(Live));
            Assert.Equal(before, await File.ReadAllBytesAsync(marker));
        }
    }

    [Theory]
    [InlineData("http://example.invalid", false)]
    [InlineData("https://example.invalid", true)]
    [InlineData("http://localhost:8000", true)]
    [InlineData("http://127.0.0.1:8000", true)]
    [InlineData("http://[::1]:8000", true)]
    [InlineData("http://localhost.example.invalid", false)]
    public void BothApiClientsEnforceTransportPolicy(string url, bool accepted)
    {
        Assert.Equal(accepted, DiscordVoiceStatusClient.TryBuildEndpoint(url, out _));
        Assert.Equal(accepted, DiscordGuildIntersectionClient.TryBuildEndpoint(url, out _));
    }

    private static string Session(string account, string token) =>
        "refresh_token: " + token + "\nid_token: e30." +
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new { iss = "https://fixture.invalid", sub = account })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";

    private sealed class ResistantProcesses(bool denied) : IProcessService
    {
        public Task<bool> IsRunningAsync(string name, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task StopAsync(string name, CancellationToken cancellationToken) => denied
            ? Task.FromException(new System.ComponentModel.Win32Exception(5)) : Task.CompletedTask;
        public Task StartAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> BringToFrontAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
