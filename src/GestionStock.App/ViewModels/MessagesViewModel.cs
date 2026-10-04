using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>Une conversation (un numéro) : affichée dans la liste de gauche, façon application Messages.</summary>
public record ConversationRow(string Phone, string LastMessagePreview, DateTime LastDate);

/// <summary>Un message affiché comme une bulle dans le fil de la conversation sélectionnée.</summary>
public record MessageBubble(string Content, DateTime Date, bool IsOutgoing);

/// <summary>
/// Messages des puces opérateurs : lit les SMS envoyés et reçus d'un modem Huawei en mode HiLink (via son API web,
/// configurée dans Paramètres) et les affiche façon application Messages, groupés par conversation. Lecture seule,
/// et toujours sur demande explicite (bouton « Récupérer les messages »), jamais automatique.
/// </summary>
public partial class MessagesViewModel : ViewModelBase
{
    private readonly HiLinkMessagesService _hiLink;
    private readonly SettingsService _settings;
    private List<HiLinkMessage> _allMessages = new();

    public MessagesViewModel(HiLinkMessagesService hiLink, SettingsService settings)
    {
        _hiLink = hiLink;
        _settings = settings;
        _ = InitializeAsync();
    }

    public ObservableCollection<Operator> Operators { get; } = new();
    public ObservableCollection<ConversationRow> Conversations { get; } = new();
    public ObservableCollection<MessageBubble> Thread { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private Operator? _selectedOperator;
    [ObservableProperty] private ConversationRow? _selectedConversation;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;
    [ObservableProperty] private string _status = "";

    public string SelectedConversationTitle => SelectedConversation == null ? "" : $"Conversation avec {SelectedConversation.Phone}";

    partial void OnSelectedOperatorChanged(Operator? value)
    {
        Conversations.Clear();
        Thread.Clear();
        _allMessages.Clear();
        Status = "";
    }

    partial void OnSelectedConversationChanged(ConversationRow? value)
    {
        OnPropertyChanged(nameof(SelectedConversationTitle));
        Thread.Clear();
        if (value == null) return;
        foreach (var m in _allMessages.Where(m => m.Phone == value.Phone).OrderBy(m => m.Date))
            Thread.Add(new MessageBubble(m.Content, m.Date, m.IsOutgoing));
    }

    private async Task InitializeAsync()
    {
        await TryAsync(async () =>
        {
            foreach (var op in await _settings.GetOperatorsAsync()) Operators.Add(op);
            SelectedOperator = Operators.FirstOrDefault();
        });
    }

    private bool CanRefresh() => !IsLoading && SelectedOperator != null;

    /// <summary>Interroge le modem de l'opérateur choisi et reconstruit la liste des conversations. Jamais automatique.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (SelectedOperator == null) return;
        if (string.IsNullOrWhiteSpace(SelectedOperator.HiLinkHost))
        {
            Info($"Aucune adresse IP configurée pour {SelectedOperator.Name} (page Paramètres > IP du modem).");
            return;
        }

        IsLoading = true;
        Status = "Récupération des messages…";
        Conversations.Clear();
        Thread.Clear();
        SelectedConversation = null;
        try
        {
            var result = await _hiLink.GetMessagesAsync(SelectedOperator.HiLinkHost);
            if (!result.Success)
            {
                Status = result.Error ?? "Échec de la récupération.";
                return;
            }

            _allMessages = result.Messages;
            foreach (var g in _allMessages.GroupBy(m => m.Phone).OrderByDescending(g => g.Max(m => m.Date)))
            {
                var last = g.OrderByDescending(m => m.Date).First();
                Conversations.Add(new ConversationRow(g.Key, last.Content, last.Date));
            }
            Status = _allMessages.Count == 0
                ? "Aucun message trouvé."
                : $"{_allMessages.Count} message(s) sur {Conversations.Count} conversation(s).";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
