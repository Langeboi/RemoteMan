using RemoteMan.Core.Security;
using Xunit;

namespace RemoteMan.Tests;

public class TotpTests
{
    // RFC 6238 appendix B seeds, base32: 20, 32 and 64 ASCII bytes of "1234567890…".
    private const string Sha1Key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string Sha256Key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA";
    private const string Sha512Key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA";

    [Theory]
    [InlineData(59L, "SHA256", "46119246")]
    [InlineData(59L, "SHA512", "90693936")]
    [InlineData(1111111109L, "SHA256", "68084774")]
    [InlineData(1111111109L, "SHA512", "25091201")]
    [InlineData(20000000000L, "SHA256", "77737706")]
    [InlineData(20000000000L, "SHA512", "47863826")]
    public void FollowsTheLinksHashAlgorithm(long unix, string algo, string expected)
    {
        var key = algo == "SHA256" ? Sha256Key : Sha512Key;
        var link = $"otpauth://totp/lab:me?secret={key}&algorithm={algo}&digits=8";
        Assert.Equal(expected, Totp.Code(link, DateTimeOffset.FromUnixTimeSeconds(unix)));
    }

    [Fact]
    public void FollowsTheLinksPeriod()
    {
        var link = $"otpauth://totp/x?secret={Sha1Key}&period=60";
        Assert.Equal(60, Totp.Period(link));
        Assert.Equal(1, Totp.SecondsLeft(link, DateTimeOffset.FromUnixTimeSeconds(119)));
        // Same 60 s window, same code; a 30 s key would have changed at 90.
        Assert.Equal(Totp.Code(link, DateTimeOffset.FromUnixTimeSeconds(61)), Totp.Code(link, DateTimeOffset.FromUnixTimeSeconds(119)));
    }

    [Fact]
    public void CopyHandsOverTheNextCodeWhenTheCurrentOneIsAboutToExpire()
    {
        var early = Totp.CodeToCopy(Sha1Key, out var left1, DateTimeOffset.FromUnixTimeSeconds(40));
        Assert.Equal(Totp.Code(Sha1Key, DateTimeOffset.FromUnixTimeSeconds(40)), early);
        Assert.Equal(20, left1);

        var late = Totp.CodeToCopy(Sha1Key, out var left2, DateTimeOffset.FromUnixTimeSeconds(58));
        Assert.Equal(Totp.Code(Sha1Key, DateTimeOffset.FromUnixTimeSeconds(60)), late);
        Assert.Equal(32, left2);
    }

    [Theory]
    [InlineData("otpauth://totp/GitHub:lange?secret=JBSWY3DPEHPK3PXP&issuer=GitHub", "GitHub (lange)")]
    [InlineData("otpauth://totp/ACME%20Co:john%40example.com?secret=JBSWY3DPEHPK3PXP", "ACME Co (john@example.com)")]
    [InlineData("otpauth://totp/pve?secret=JBSWY3DPEHPK3PXP&issuer=Proxmox", "Proxmox (pve)")]
    [InlineData("JBSWY3DPEHPK3PXP", "")]
    public void LabelsFromTheLink(string secret, string label) => Assert.Equal(label, Totp.Label(secret));

    [Theory]
    [InlineData("otpauth://hotp/x?secret=JBSWY3DPEHPK3PXP&counter=1")]
    [InlineData("otpauth://totp/x?digits=6")]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&algorithm=MD5")]
    [InlineData("https://example.com/?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("")]
    public void RejectsWhatItCantUse(string secret)
    {
        Assert.False(Totp.IsValid(secret));
        Assert.NotNull(Totp.Validate(secret));
    }

    [Theory]
    [InlineData("123456", "123 456")]
    [InlineData("12345678", "1234 5678")]
    public void GroupsCodesForReading(string code, string grouped) => Assert.Equal(grouped, Totp.Group(code));
}
