using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GestionStock.Puces.ViewModels;

public record Bubble(string Text, DateTime? Date, bool IsOutgoing);

public class Conversation : ObservableObject
{
    public Conversation(string title) => Title = title;

    public string Title { get; }
    public ObservableCollection<Bubble> Messages { get; } = new();
    public string Preview => Messages.LastOrDefault()?.Text ?? "";
    public DateTime? LastDate => Messages.LastOrDefault()?.Date;

    public void Add(Bubble bubble)
    {
        Messages.Add(bubble);
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(LastDate));
    }
}

public partial class OperatorChat : ObservableObject
{
    private readonly Conversation _ussd = new("Requêtes USSD");

    public OperatorChat(string name, string? port, string balanceCode)
    {
        Name = name;
        Port = port;
        BalanceCode = balanceCode;
        Conversations.Add(_ussd);
        SelectedConversation = _ussd;
    }

    public string Name { get; }
    public string? Port { get; }
    public string BalanceCode { get; }
    public ObservableCollection<Conversation> Conversations { get; } = new();

    /// <summary>Null tant que la connexion n'a pas été testée ; vrai si le modem a répondu ; faux sinon.</summary>
    [ObservableProperty] private bool? _isReachable;
    [ObservableProperty] private Conversation? _selectedConversation;
    [ObservableProperty] private string _ussdCode = "";

    /// <summary>Remplace les conversations de SMS reçus (de la SIM, ou de l'archive locale), en gardant les requêtes USSD.</summary>
    public void ReplaceReceivedMessages(IEnumerable<(string Phone, DateTime? Date, string Body)> received)
    {
        var selectedTitle = SelectedConversation?.Title;
        for (var i = Conversations.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(Conversations[i], _ussd)) Conversations.RemoveAt(i);

        foreach (var group in received.GroupBy(m => m.Phone).OrderByDescending(g => g.Max(m => m.Date)))
        {
            var conversation = new Conversation(group.Key);
            foreach (var m in group.OrderBy(m => m.Date)) conversation.Add(new Bubble(m.Body, m.Date, false));
            Conversations.Add(conversation);
        }

        SelectedConversation = Conversations.FirstOrDefault(c => c.Title == selectedTitle) ?? _ussd;
    }

    public void AddUssdExchange(string code, string reply)
    {
        _ussd.Add(new Bubble(code, DateTime.Now, true));
        _ussd.Add(new Bubble(reply, DateTime.Now, false));
        SelectedConversation = _ussd;
    }
}
