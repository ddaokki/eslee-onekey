namespace Eslee.OneKey.Infrastructure.Windows;

public static class DiscordApiUrlPolicy
{
    public static bool TryValidate(string value, out Uri? uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return false;
        return string.IsNullOrEmpty(uri.UserInfo) &&
            (uri.Scheme == Uri.UriSchemeHttps ||
             (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
    }

    public static void ValidateOptional(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !TryValidate(value, out _))
            throw new ArgumentException("봇 API는 HTTPS 주소가 필요합니다. HTTP는 localhost/loopback 개발 주소만 허용합니다.");
    }
}
