using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

public sealed class AccountMaintenanceTests
{
    [Fact]
    public async Task RecoveryWaitsForEntireActivateLaunchConfirmAcrossEngineInstances()
    {
        var sessions = new FakeGameSessionService();
        var processes = new PausedStartProcesses();
        var engine = new AutomationEngine(
            new AutomationSettings { LaunchExecutablePath = "fake.exe" },
            new FakeAudioService(), processes, new FakeVoiceClient([]),
            new FakeSessionStore(), new FakeClock(), new FakeLogger(), accountSessions: sessions);
        var profile = new GameAccountProfile();
        var switching = engine.SwitchAccountAsync(profile);
        await processes.Entered.Task;
        Assert.Single(sessions.Activated); // activation completed, launcher start is suspended
        Assert.Empty(sessions.Confirmed);
        var recovered = false;
        var maintenance = AutomationEngine.RunAccountMaintenanceAsync(() =>
        {
            Assert.Single(sessions.Confirmed); // cannot restore B while A is still launching
            recovered = true;
            return Task.FromResult(true);
        });
        Assert.False(maintenance.IsCompleted);
        Assert.False(recovered);
        processes.Resume.SetResult();
        Assert.True((await switching).Started);
        Assert.True(await maintenance);
        Assert.True(recovered);
    }

    [Fact]
    public async Task FailedMaintenanceReleasesGateForTheNextOperation()
    {
        await Assert.ThrowsAsync<IOException>(() =>
            AutomationEngine.RunAccountMaintenanceAsync<bool>(() => throw new IOException("fake")));
        Assert.True(await AutomationEngine.RunAccountMaintenanceAsync(() => Task.FromResult(true)));
    }

    private sealed class PausedStartProcesses : IProcessService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task StartAsync(string path, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Resume.Task;
        }
        public Task<bool> IsRunningAsync(string name, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task StopAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> BringToFrontAsync(string name, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
