using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class PinyinAndProtocolTests
{
    [Theory]
    [InlineData("备忘录", "bwl", true)]
    [InlineData("备忘录", "beiwanglu", true)]
    [InlineData("微信", "wx", true)]
    [InlineData("微信", "weixin", true)]
    [InlineData("记事本", "jsb", true)]
    [InlineData("终端", "zd", true)]
    [InlineData("Visual Studio Code", "vscode", false)]
    [InlineData("Visual Studio Code", "studio", true)]
    public void PinyinSearch_MatchesChineseAndInitials(string name, string query, bool expected)
    {
        Assert.Equal(expected, PinyinSearch.Matches(name, query));
    }

    [Fact]
    public void VirtualDisplayProtocol_MatchesDriverIpcSchema()
    {
        var monitor = new VddMonitor(
            200_000_108,
            "Snug 手机屏",
            true,
            [new VddMode(1080, 1900, [60])]);

        string notifyJson = VirtualDisplayIpcClient.SerializeNotify([monitor]);
        Assert.Contains("\"Notify\":[", notifyJson);
        Assert.Contains("\"id\":200000108", notifyJson);
        Assert.Contains("\"refresh_rates\":[60]", notifyJson);

        string legacyAddJson = VirtualDisplayIpcClient.SerializeLegacyAdd([monitor]);
        Assert.Contains("\"Add\":[", legacyAddJson);
        Assert.Contains("\"refresh_rate\":60", legacyAddJson);

        Assert.Equal("{\"Remove\":[200000108]}", VirtualDisplayIpcClient.SerializeRemove([200_000_108]));
        Assert.Equal("\"RemoveAll\"", VirtualDisplayIpcClient.SerializeRemoveAll());
        Assert.Equal("\"State\"", VirtualDisplayIpcClient.SerializeStateRequest());

        string replyJson = "{\"State\":[{\"id\":200000108,\"name\":\"Snug 手机屏\",\"enabled\":true,\"modes\":[{\"width\":1080,\"height\":1900,\"refresh_rates\":[60]}]}]}";
        Assert.True(VirtualDisplayIpcClient.TryParseStateReply(replyJson, out var parsed));
        Assert.Single(parsed);
        Assert.Equal(200_000_108u, parsed[0].Id);
        Assert.Equal(1080u, parsed[0].Modes[0].Width);
    }
}
