using System.Diagnostics;
using System.Text.Json;
using Eslee.OneKey.Core;
using Eslee.OneKey.Infrastructure.Windows;

namespace Eslee.OneKey.Tests;

/// <summary>
/// 실제 Discord 클라이언트와 저장된 RPC 인증이 있는 PC에서만 의미가 있는 smoke test.
/// 연속 재연결에서 간헐적으로 실패하던 문제를 실물로 확인합니다.
/// 대상 채널을 바꾸지 않으며(이미 그 채널이면 무동작), 강제 이동도 하지 않습니다.
/// ONEKEY_DISCORD_RPC_SMOKE=1 일 때만 실행됩니다.
/// </summary>
public sealed class DiscordRpcSmokeTests
{
    [DiscordRpcSmokeFact]
    public async Task ConsecutiveReconnectsAllSucceed()
    {
        var paths = new ApplicationPaths(Environment.GetEnvironmentVariable("ONEKEY_SMOKE_PROFILE_ROOT"));
        var settings = (await new JsonSettingsStore(paths).LoadAsync(CancellationToken.None))
            .Automations.FirstOrDefault();
        Assert.NotNull(settings);
        Assert.False(string.IsNullOrWhiteSpace(settings.DiscordRpcClientId));

        var secrets = new DpapiSecretStore(paths);
        await using var client = new DiscordRpcVoiceChannelClient(
            settings.DiscordRpcClientId,
            async cancellationToken => JsonSerializer.Deserialize<DiscordRpcTokens>(
                await secrets.LoadRpcSecretsAsync(cancellationToken) ?? "")?.AccessToken,
            TimeSpan.FromSeconds(30));

        // 같은 클라이언트로 연속 연결 — 예전에는 2회차에서 20초를 소진하고 실패했다.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var connection = await client.ConnectAsync(CancellationToken.None);
            Assert.Equal(DiscordRpcStatus.Connected, connection.Status);
            await client.GetSelectedVoiceChannelIdAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 반복 호출이 무동작인지 확인합니다. 이 테스트는 사용자를 음성채널로
    /// 끌어들이면 안 되므로 채널 선택 호출을 차단합니다. 대상 채널 밖이면
    /// 성공으로 숨기지 않고 전제 조건 미충족을 보고합니다.
    /// </summary>
    [DiscordRpcSmokeFact]
    public async Task RepeatedAutoJoinIsIdempotentWhenAlreadyInTheTargetChannel()
    {
        var paths = new ApplicationPaths(Environment.GetEnvironmentVariable("ONEKEY_SMOKE_PROFILE_ROOT"));
        var settings = (await new JsonSettingsStore(paths).LoadAsync(CancellationToken.None))
            .Automations.First();
        var secrets = new DpapiSecretStore(paths);
        await using var client = new DiscordRpcVoiceChannelClient(
            settings.DiscordRpcClientId,
            async cancellationToken => JsonSerializer.Deserialize<DiscordRpcTokens>(
                await secrets.LoadRpcSecretsAsync(cancellationToken) ?? "")?.AccessToken,
            TimeSpan.FromSeconds(30));

        var connection = await client.ConnectAsync(CancellationToken.None);
        Assert.Equal(DiscordRpcStatus.Connected, connection.Status);

        var current = await client.GetSelectedVoiceChannelIdAsync(CancellationToken.None);
        DiscordChannelTarget.TryParse(settings.VoiceChannelTarget, out var target);
        Assert.True(current is not null && current == target,
            "Read-only smoke prerequisite: already in the configured target channel. No channel was changed.");

        // Even if the user leaves between the first check and the next RPC query, never SELECT.
        var join = new VoiceChannelAutoJoin(new ReadOnlySmokeVoiceClient(client), new FakeLogger());
        for (var round = 1; round <= 3; round++)
        {
            var result = await join.EnsureJoinedAsync(settings, CancellationToken.None);
            Assert.Equal(VoiceJoinOutcome.AlreadyInTargetChannel, result.Outcome);
        }
    }
}

public sealed class DiscordRpcSmokeFactAttribute : FactAttribute
{
    public DiscordRpcSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ONEKEY_DISCORD_RPC_SMOKE") != "1")
        {
            Skip = "실제 Discord RPC smoke test는 ONEKEY_DISCORD_RPC_SMOKE=1 설정 시에만 실행됩니다.";
            return;
        }
        var running = false;
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                running = true;
                process.Dispose();
            }
        }
        if (!running)
            Skip = "Read-only RPC smoke unavailable: no running Discord client. No client or channel was started.";
    }
}

internal sealed class ReadOnlySmokeVoiceClient(IDiscordVoiceChannelClient inner) : IDiscordVoiceChannelClient
{
    public Task<DiscordRpcConnection> ConnectAsync(CancellationToken ct) => inner.ConnectAsync(ct);
    public Task<DiscordRpcConnection> EnsureConnectedAsync(CancellationToken ct) => inner.EnsureConnectedAsync(ct);
    public Task DisconnectAsync(CancellationToken ct) => inner.DisconnectAsync(ct);
    public Task<string?> GetSelectedVoiceChannelIdAsync(CancellationToken ct) => inner.GetSelectedVoiceChannelIdAsync(ct);
    public Task<IReadOnlyList<DiscordGuild>> GetGuildsAsync(CancellationToken ct) => inner.GetGuildsAsync(ct);
    public Task<IReadOnlyList<DiscordVoiceChannel>> GetVoiceChannelsAsync(string guildId, CancellationToken ct) => inner.GetVoiceChannelsAsync(guildId, ct);
    public Task SelectVoiceChannelAsync(string channelId, CancellationToken ct) =>
        throw new InvalidOperationException("Read-only smoke refused a channel selection after state changed.");
}

public sealed class DiscordRpcSmokeSafetyTests
{
    [Fact]
    public async Task LeavingChannelBetweenChecksCannotCauseARealJoin()
    {
        var inner = new FakeVoiceChannelClient { CurrentChannelId = null };
        var join = new VoiceChannelAutoJoin(new ReadOnlySmokeVoiceClient(inner), new FakeLogger());
        await Assert.ThrowsAsync<InvalidOperationException>(() => join.EnsureJoinedAsync(
            new AutomationSettings
            {
                UseDiscordIntegration = true, AutoJoinVoiceChannel = true,
                VoiceChannelTarget = "123456789012345678",
            }, CancellationToken.None));
        Assert.Empty(inner.SelectedChannels);
    }
}
