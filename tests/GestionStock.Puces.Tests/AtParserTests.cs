using GestionStock.Puces.Modem;

namespace GestionStock.Puces.Tests;

public class AtParserTests
{
    [Fact]
    public void DecodeIfUcs2Hex_decodes_ucs2_hex_text()
    {
        Assert.Equal("ABCD", AtParser.DecodeIfUcs2Hex("0041004200430044"));
    }

    [Theory]
    [InlineData("0660223130")]
    [InlineData("+213660223130")]
    [InlineData("Solde 100 DA")]
    [InlineData("1234")]
    public void DecodeIfUcs2Hex_leaves_phone_numbers_and_plain_text_alone(string text)
    {
        Assert.Equal(text, AtParser.DecodeIfUcs2Hex(text));
    }

    [Fact]
    public void ParseCusd_reads_status_and_decoded_text()
    {
        var reply = AtParser.ParseCusd("+CUSD: 2,\"004F004B\",15");
        Assert.Equal((2, "OK"), reply);
    }

    [Fact]
    public void ParseCusd_returns_null_for_unrelated_line()
    {
        Assert.Null(AtParser.ParseCusd("OK"));
    }

    [Fact]
    public void ParseCmgl_reads_received_and_sent_messages_with_bodies()
    {
        var lines = new[]
        {
            "+CMGL: 1,\"REC READ\",\"+213660223130\",,\"26/10/05,22:17:00+04\"",
            "Solde 100 DA",
            "+CMGL: 2,\"STO SENT\",\"0660223130\",,\"26/10/05,22:20:00+04\"",
            "Bonjour",
            "OK",
        };

        var messages = AtParser.ParseCmgl(lines);

        Assert.Equal(2, messages.Count);
        Assert.Equal(new SmsMessage(1, "+213660223130", new DateTime(2026, 10, 5, 22, 17, 0), "Solde 100 DA", false), messages[0]);
        Assert.Equal(new SmsMessage(2, "0660223130", new DateTime(2026, 10, 5, 22, 20, 0), "Bonjour", true), messages[1]);
    }

    [Fact]
    public void ParseCmgl_decodes_ucs2_hex_body_and_phone()
    {
        var lines = new[]
        {
            "+CMGL: 3,\"REC UNREAD\",\"0036003600300032003200330031003300300030\",,\"26/10/05,09:00:00+04\"",
            "00530061006C00640065",
            "OK",
        };

        var message = Assert.Single(AtParser.ParseCmgl(lines));

        Assert.Equal("6602231300", message.Phone);
        Assert.Equal("Salde", message.Body);
    }

    [Fact]
    public void ParseCmgl_returns_empty_list_when_storage_is_empty()
    {
        Assert.Empty(AtParser.ParseCmgl(new[] { "OK" }));
    }
}
