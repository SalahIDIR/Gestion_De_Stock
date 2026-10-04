using System.Net;
using System.Text;
using System.Xml.Linq;

namespace GestionStock.Core.Services;

/// <summary>Un SMS lu sur un modem HiLink : reçu ou envoyé.</summary>
public record HiLinkMessage(string Phone, string Content, DateTime Date, bool IsOutgoing);

/// <summary>Résultat d'une lecture des SMS d'un modem HiLink.</summary>
public record HiLinkResult(bool Success, List<HiLinkMessage> Messages, string? Error);

/// <summary>
/// Échange HTTP brut avec un modem HiLink (GET/POST renvoyant du XML). Abstraction pour tester l'orchestration de
/// <see cref="HiLinkMessagesService"/> sans matériel réel.
/// </summary>
public interface IHiLinkTransport
{
    Task<string> GetAsync(string host, string path);
    Task<string> PostAsync(string host, string path, string xmlBody, string token);
}

/// <summary>
/// Implémentation réelle par HTTP. Un modem HiLink garde une session par cookie : un client HTTP (avec son propre
/// pot de cookies) est réutilisé par hôte, pour que le jeton obtenu reste valide d'un appel à l'autre.
/// </summary>
public class HiLinkHttpTransport : IHiLinkTransport, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, HttpClient> _clients = new();

    private HttpClient GetClient(string host)
    {
        if (_clients.TryGetValue(host, out var existing)) return existing;
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{host}/"), Timeout = Timeout };
        _clients[host] = client;
        return client;
    }

    public Task<string> GetAsync(string host, string path) => GetClient(host).GetStringAsync(path);

    public async Task<string> PostAsync(string host, string path, string xmlBody, string token)
    {
        using var content = new StringContent(xmlBody, Encoding.UTF8, "application/xml");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("__RequestVerificationToken", token);
        using var response = await GetClient(host).SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
    }
}

/// <summary>
/// Lit les SMS envoyés et reçus d'un modem Huawei en mode HiLink (ex. E3372h-153), via l'API web locale du modem
/// (http://&lt;ip&gt;/api/...) : ce mode n'expose pas de port COM / commandes AT, contrairement au mode « Stick ».
/// N'a pas pu être testé sur un modem physique : suit le protocole documenté par la communauté (session/jeton via
/// /api/webserver/SesTokInfo, liste des SMS via /api/sms/sms-list), mais certains firmwares se comportent
/// différemment (jeton à usage unique par exemple) et le protocole devra probablement être ajusté une fois essayé
/// avec du vrai matériel.
/// </summary>
public class HiLinkMessagesService
{
    private readonly IHiLinkTransport _transport;

    public HiLinkMessagesService(IHiLinkTransport transport) => _transport = transport;

    /// <summary>Lit la boîte de réception et les messages envoyés du modem à cette adresse, triés du plus récent au plus ancien.</summary>
    public async Task<HiLinkResult> GetMessagesAsync(string host)
    {
        string token;
        try
        {
            var sessionXml = await _transport.GetAsync(host, "api/webserver/SesTokInfo");
            token = ExtractToken(sessionXml)
                    ?? throw new InvalidOperationException("Jeton de session introuvable dans la réponse du modem.");
        }
        catch (Exception ex)
        {
            return new HiLinkResult(false, [], $"Impossible de joindre le modem à {host} : {ex.Message}");
        }

        try
        {
            var inboxXml = await _transport.PostAsync(host, "api/sms/sms-list", BuildSmsListBody(BoxTypeInbox), token);
            var sentXml = await _transport.PostAsync(host, "api/sms/sms-list", BuildSmsListBody(BoxTypeSent), token);
            var messages = ParseMessages(inboxXml, isOutgoing: false)
                .Concat(ParseMessages(sentXml, isOutgoing: true))
                .OrderByDescending(m => m.Date)
                .ToList();
            return new HiLinkResult(true, messages, null);
        }
        catch (Exception ex)
        {
            return new HiLinkResult(false, [], $"Erreur lors de la lecture des SMS : {ex.Message}");
        }
    }

    private const int BoxTypeInbox = 1;
    private const int BoxTypeSent = 2;

    internal static string? ExtractToken(string sessionXml)
    {
        try { return XDocument.Parse(sessionXml).Root?.Element("TokInfo")?.Value; }
        catch { return null; }
    }

    internal static string BuildSmsListBody(int boxType) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<request>" +
        "<PageIndex>1</PageIndex><ReadCount>250</ReadCount>" +
        $"<BoxType>{boxType}</BoxType><SortType>0</SortType><Ascending>0</Ascending><UnreadPreferred>0</UnreadPreferred>" +
        "</request>";

    /// <summary>Analyse la réponse XML d'une boîte SMS. Pure, sans réseau : testable directement.</summary>
    internal static List<HiLinkMessage> ParseMessages(string xml, bool isOutgoing)
    {
        var doc = XDocument.Parse(xml);
        var messages = new List<HiLinkMessage>();
        foreach (var msg in doc.Descendants("Message"))
        {
            var phone = msg.Element("Phone")?.Value ?? "";
            var content = msg.Element("Content")?.Value ?? "";
            var date = DateTime.TryParse(msg.Element("Date")?.Value, out var parsed) ? parsed : DateTime.MinValue;
            messages.Add(new HiLinkMessage(phone, content, date, isOutgoing));
        }
        return messages;
    }
}
