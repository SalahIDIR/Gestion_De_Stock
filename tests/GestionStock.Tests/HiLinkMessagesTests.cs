using GestionStock.Core.Services;

namespace GestionStock.Tests;

/// <summary>Transport factice : ne touche à aucun réseau. Renvoie des réponses XML programmées par chemin.</summary>
public class FakeHiLinkTransport : IHiLinkTransport
{
    public Dictionary<string, string> GetResponses { get; } = new();
    public Dictionary<int, string> SmsListResponsesByBoxType { get; } = new();
    public List<(string Host, string Path)> GetCalls { get; } = new();
    public List<(string Host, string Path, string Body, string Token)> PostCalls { get; } = new();
    public Func<string, string, Task<string>>? GetOverride { get; set; }

    public Task<string> GetAsync(string host, string path)
    {
        GetCalls.Add((host, path));
        if (GetOverride != null) return GetOverride(host, path);
        return Task.FromResult(GetResponses.TryGetValue(path, out var r) ? r : throw new InvalidOperationException($"Pas de réponse programmée pour {path}"));
    }

    public Task<string> PostAsync(string host, string path, string xmlBody, string token)
    {
        PostCalls.Add((host, path, xmlBody, token));
        var boxType = xmlBody.Contains("<BoxType>1<") ? 1 : xmlBody.Contains("<BoxType>2<") ? 2 : -1;
        return Task.FromResult(SmsListResponsesByBoxType.TryGetValue(boxType, out var r) ? r : "<response><Count>0</Count><Messages></Messages></response>");
    }
}

public class HiLinkMessagesTests
{
    private const string SessionXml = "<response><SesInfo>SessionID=abc</SesInfo><TokInfo>my-token</TokInfo></response>";

    private const string InboxXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <response><Count>1</Count><Messages>
        <Message><Smstat>1</Smstat><Index>1</Index><Phone>0778123456</Phone><Content>Bonjour</Content><Date>2026-10-04 10:00:00</Date></Message>
        </Messages></response>
        """;

    private const string SentXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <response><Count>1</Count><Messages>
        <Message><Smstat>1</Smstat><Index>2</Index><Phone>0778123456</Phone><Content>Salut</Content><Date>2026-10-04 10:05:00</Date></Message>
        </Messages></response>
        """;

    [Fact]
    public async Task GetMessagesAsync_fetches_the_token_then_both_boxes_and_merges_them_newest_first()
    {
        var transport = new FakeHiLinkTransport();
        transport.GetResponses["api/webserver/SesTokInfo"] = SessionXml;
        transport.SmsListResponsesByBoxType[1] = InboxXml;
        transport.SmsListResponsesByBoxType[2] = SentXml;
        var service = new HiLinkMessagesService(transport);

        var result = await service.GetMessagesAsync("192.168.8.1");

        Assert.True(result.Success);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("Salut", result.Messages[0].Content); // le plus récent (10:05) en premier
        Assert.True(result.Messages[0].IsOutgoing);
        Assert.Equal("Bonjour", result.Messages[1].Content);
        Assert.False(result.Messages[1].IsOutgoing);

        // Le jeton de session est bien transmis aux deux requêtes POST.
        Assert.All(transport.PostCalls, c => Assert.Equal("my-token", c.Token));
        Assert.Equal(2, transport.PostCalls.Count);
    }

    [Fact]
    public async Task GetMessagesAsync_fails_cleanly_when_the_modem_is_unreachable()
    {
        var transport = new FakeHiLinkTransport
        {
            GetOverride = (_, _) => throw new HttpRequestException("Connexion refusée"),
        };
        var service = new HiLinkMessagesService(transport);

        var result = await service.GetMessagesAsync("192.168.8.1");

        Assert.False(result.Success);
        Assert.Empty(result.Messages);
        Assert.Contains("192.168.8.1", result.Error);
    }

    [Fact]
    public async Task GetMessagesAsync_fails_cleanly_when_the_token_is_missing_from_the_response()
    {
        var transport = new FakeHiLinkTransport();
        transport.GetResponses["api/webserver/SesTokInfo"] = "<response><SesInfo>SessionID=abc</SesInfo></response>"; // pas de TokInfo
        var service = new HiLinkMessagesService(transport);

        var result = await service.GetMessagesAsync("192.168.8.1");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ExtractToken_reads_the_TokInfo_element()
    {
        Assert.Equal("my-token", HiLinkMessagesService.ExtractToken(SessionXml));
    }

    [Fact]
    public void ExtractToken_returns_null_on_malformed_xml()
    {
        Assert.Null(HiLinkMessagesService.ExtractToken("not xml at all"));
    }

    [Fact]
    public void BuildSmsListBody_includes_the_requested_box_type()
    {
        Assert.Contains("<BoxType>1</BoxType>", HiLinkMessagesService.BuildSmsListBody(1));
        Assert.Contains("<BoxType>2</BoxType>", HiLinkMessagesService.BuildSmsListBody(2));
    }

    [Fact]
    public void ParseMessages_reads_phone_content_and_date_from_each_message()
    {
        var messages = HiLinkMessagesService.ParseMessages(InboxXml, isOutgoing: false);

        var message = Assert.Single(messages);
        Assert.Equal("0778123456", message.Phone);
        Assert.Equal("Bonjour", message.Content);
        Assert.Equal(new DateTime(2026, 10, 4, 10, 0, 0), message.Date);
        Assert.False(message.IsOutgoing);
    }

    [Fact]
    public void ParseMessages_returns_an_empty_list_when_there_are_no_messages()
    {
        var messages = HiLinkMessagesService.ParseMessages("<response><Count>0</Count><Messages></Messages></response>", isOutgoing: true);
        Assert.Empty(messages);
    }
}
