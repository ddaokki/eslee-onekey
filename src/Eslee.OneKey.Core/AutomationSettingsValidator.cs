namespace Eslee.OneKey.Core;

/// <summary>Only enabled features require their inputs. Editor toggles also cover an empty, newly enabled feature.</summary>
public static class AutomationSettingsValidator
{
    public static void Validate(AutomationSettings settings, bool? useProgram = null, bool? useAudio = null)
    {
        void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException($"{settings.Name}: {message}");
        }

        Require(!string.IsNullOrWhiteSpace(settings.Name), "자동화 이름을 입력하세요. 예: 게임 시작");
        if (!settings.Enabled)
            return;

        Require(!string.IsNullOrWhiteSpace(settings.Hotkey.Key), "단축키의 마지막 키를 입력하세요. 예: F8");
        if (useProgram ?? !string.IsNullOrWhiteSpace(settings.LaunchExecutablePath))
        {
            Require(!string.IsNullOrWhiteSpace(settings.LaunchExecutablePath),
                "프로그램 실행을 켰습니다. ‘찾기’로 실행할 .exe 파일을 선택하세요.");
            Require(!string.IsNullOrWhiteSpace(settings.WatchProcessName),
                "종료 감시 프로그램을 입력하세요. 작업 관리자 → 세부 정보의 이름을 사용하세요. 예: game.exe");
        }
        if (useAudio ?? !string.IsNullOrWhiteSpace(settings.TargetAudioEndpointId))
            Require(!string.IsNullOrWhiteSpace(settings.TargetAudioEndpointId),
                "오디오 장치 변경을 켰습니다. 사용할 출력 장치를 목록에서 선택하세요.");

        if (settings.UseDiscordIntegration && settings.AutoJoinVoiceChannel)
            Require(DiscordChannelTarget.TryParse(settings.VoiceChannelTarget, out _),
                "음성채널 자동 입장을 켰습니다. 채널을 선택하거나 채널 링크 / ID를 입력하세요.");
    }
}
