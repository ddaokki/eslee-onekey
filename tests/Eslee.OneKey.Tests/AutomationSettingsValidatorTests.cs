using Eslee.OneKey.Core;

namespace Eslee.OneKey.Tests;

public sealed class AutomationSettingsValidatorTests
{
    private static AutomationSettings Rule => new() { Name = "게임 시작", Hotkey = new(true, true, false, false, "F8") };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OnlyCheckedFeaturesRequireInputs(bool program, bool audio)
    {
        var rule = Rule with
        {
            LaunchExecutablePath = program ? "game.exe" : "",
            WatchProcessName = program ? "game" : "",
            TargetAudioEndpointId = audio ? "headset" : "",
        };
        AutomationSettingsValidator.Validate(rule, program, audio);
        AutomationSettingsValidator.Validate(rule); // saved rules use the same policy
    }

    [Fact]
    public void NewlyCheckedProgramWithNoPathIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => AutomationSettingsValidator.Validate(Rule, true, false));

    [Fact]
    public void ProgramWithNoWatchedProcessIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => AutomationSettingsValidator.Validate(Rule with { LaunchExecutablePath = "game.exe" }));

    [Fact]
    public void NewlyCheckedAudioWithNoDeviceIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => AutomationSettingsValidator.Validate(Rule, false, true));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnusedVoiceChannelDoesNotPreventSaving(bool discord, bool autoJoin) =>
        AutomationSettingsValidator.Validate(Rule with { UseDiscordIntegration = discord, AutoJoinVoiceChannel = autoJoin });

    [Theory]
    [InlineData("")]
    [InlineData("https://discord.gg/example")]
    public void AutoJoinRequiresValidChannel(string channel) =>
        Assert.Throws<InvalidOperationException>(() => AutomationSettingsValidator.Validate(Rule with
        { UseDiscordIntegration = true, AutoJoinVoiceChannel = true, VoiceChannelTarget = channel }));

    [Fact]
    public void DiscordOnlyCanBeSavedWithChannel() =>
        AutomationSettingsValidator.Validate(Rule with
        { UseDiscordIntegration = true, AutoJoinVoiceChannel = true, VoiceChannelTarget = "123456789012345678" });

    [Fact]
    public void DisabledDraftOnlyNeedsName() =>
        AutomationSettingsValidator.Validate(Rule with { Enabled = false, Hotkey = new(Key: "") }, true, true);

    [Fact]
    public void EnabledRuleStillNeedsHotkey() =>
        Assert.Throws<InvalidOperationException>(() => AutomationSettingsValidator.Validate(Rule with { Hotkey = new(Key: "") }));

    [Fact]
    public void DraftStillNeedsName() =>
        Assert.Throws<InvalidOperationException>(() => AutomationSettingsValidator.Validate(Rule with { Enabled = false, Name = " " }));
}
