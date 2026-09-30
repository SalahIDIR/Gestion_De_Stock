using System.Globalization;
using System.Speech.Recognition;
using System.Text;

namespace GestionStock.App.Services;

/// <summary>Résultat d'une écoute vocale : soit la valeur reconnue, soit une raison d'échec à afficher à l'utilisateur.</summary>
public record VoiceResult(bool Success, string? Value, string? Error);

/// <summary>
/// Reconnaissance vocale hors-ligne (Windows Speech Recognition / SAPI, via System.Speech), utilisée pour
/// pré-remplir un bon de livraison par la voix. Chaque appel n'écoute qu'un seul mot ou une seule séquence de
/// chiffres, dans un vocabulaire fermé (liste de choix connue), pour une fiabilité maximale — un vocabulaire fermé
/// se reconnaît bien plus sûrement qu'une dictée libre. La voix ne fait que remplir le formulaire : c'est toujours
/// l'utilisateur qui relit et clique sur Enregistrer, jamais la voix qui valide le bon directement.
/// </summary>
public class VoiceCommandService
{
    private static readonly string[] DigitWords = ["zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf"];

    /// <summary>Vrai si un moteur de reconnaissance français (SAPI) est installé sur cette machine.</summary>
    public static bool IsFrenchAvailable => FindFrenchRecognizer() != null;

    /// <summary>Écoute une seule fois et attend un mot parmi la liste donnée (ex. un nom de client ou de produit).</summary>
    public Task<VoiceResult> ListenForChoiceAsync(IReadOnlyList<string> choices, TimeSpan timeout)
    {
        if (choices.Count == 0) return Task.FromResult(new VoiceResult(false, null, "Aucun choix disponible."));
        return Task.Run(() =>
        {
            using var engine = CreateEngine();
            if (engine == null) return NoFrenchEngineResult();
            engine.LoadGrammar(new Grammar(new GrammarBuilder(new Choices(choices.ToArray()))));
            var (text, error) = Recognize(engine, timeout);
            return text == null ? new VoiceResult(false, null, error) : new VoiceResult(true, text, null);
        });
    }

    /// <summary>Écoute une suite de chiffres dits un par un (ex. « cinq zéro zéro zéro » pour 5000) et renvoie le nombre.</summary>
    public Task<VoiceResult> ListenForDigitsAsync(TimeSpan timeout)
    {
        return Task.Run(() =>
        {
            using var engine = CreateEngine();
            if (engine == null) return NoFrenchEngineResult();

            var digit = new GrammarBuilder(new Choices(DigitWords));
            var sequence = new GrammarBuilder();
            sequence.Append(digit, 1, 8); // de 1 à 8 chiffres dits à la suite
            engine.LoadGrammar(new Grammar(sequence));

            var (text, error) = Recognize(engine, timeout);
            if (text == null) return new VoiceResult(false, null, error);

            var value = ParseDigitSequence(text);
            return value == null
                ? new VoiceResult(false, null, $"Séquence de chiffres non reconnue (« {text} »).")
                : new VoiceResult(true, value.Value.ToString(CultureInfo.InvariantCulture), null);
        });
    }

    private static SpeechRecognitionEngine? CreateEngine()
    {
        var french = FindFrenchRecognizer();
        if (french == null) return null;
        var engine = new SpeechRecognitionEngine(french.Culture);
        engine.SetInputToDefaultAudioDevice();
        return engine;
    }

    private static RecognizerInfo? FindFrenchRecognizer()
        => SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "fr");

    private static VoiceResult NoFrenchEngineResult() => new(false, null,
        "Aucune reconnaissance vocale française installée sur cette machine (Paramètres Windows > Heure et langue > Voix > Ajouter une voix > Français).");

    /// <summary>Écoute une seule reconnaissance : renvoie le texte reconnu, ou null avec un message d'erreur.</summary>
    private static (string? Text, string? Error) Recognize(SpeechRecognitionEngine engine, TimeSpan timeout)
    {
        RecognitionResult? result;
        try
        {
            result = engine.Recognize(timeout);
        }
        catch (Exception ex)
        {
            return (null, $"Erreur du microphone ou du moteur vocal : {ex.Message}");
        }
        return result == null ? (null, "Rien n'a été compris (silence ou délai dépassé).") : (result.Text, null);
    }

    /// <summary>Convertit une suite de mots-chiffres français (« cinq zéro zéro zéro ») en nombre. Pure, sans matériel : testable directement.</summary>
    internal static decimal? ParseDigitSequence(string recognizedText)
    {
        var words = recognizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        var digits = new StringBuilder();
        foreach (var w in words)
        {
            var index = Array.IndexOf(DigitWords, w.Trim().ToLowerInvariant());
            if (index < 0) return null;
            digits.Append(index);
        }
        return decimal.Parse(digits.ToString(), CultureInfo.InvariantCulture);
    }
}
