using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GestionStock.App.Services;
using GestionStock.Core.Domain;
using GestionStock.Core.Services;

namespace GestionStock.App.ViewModels;

/// <summary>Ligne en cours de saisie dans le formulaire de bon de livraison.</summary>
public partial class DeliveryLineEditor : ObservableObject
{
    [ObservableProperty] private Product? _product;
    [ObservableProperty] private string _quantityText = "";
    [ObservableProperty] private string _unitPriceText = "";
    [ObservableProperty] private ClientChip? _selectedChip;

    public ObservableCollection<ClientChip> AvailableChips { get; } = new();

    public DeliveryLineEditor() => AvailableChips.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanChooseChip));

    /// <summary>Le choix de puce n'est ouvert que si le client a plusieurs puces pour l'opérateur du produit.</summary>
    public bool CanChooseChip => AvailableChips.Count > 1;

    public decimal? Quantity => ViewModelBase.ParseDecimal(QuantityText);
    public decimal? UnitPrice => ViewModelBase.ParseDecimal(UnitPriceText);

    private bool _settingAutoPrice;

    /// <summary>Vrai si le coefficient a été proposé automatiquement (tarif du client) et pas saisi à la main.</summary>
    public bool IsPriceAutoFilled { get; private set; }

    /// <summary>Remplit le coefficient avec le tarif proposé ; il sera remplacé si le client ou le produit change.</summary>
    public void SetAutoPrice(string text)
    {
        _settingAutoPrice = true;
        UnitPriceText = text;
        _settingAutoPrice = false;
        IsPriceAutoFilled = text.Length > 0;
    }

    /// <summary>Montant facturé, ou null tant que la saisie est incomplète.</summary>
    public decimal? Total => Quantity is > 0 && UnitPrice is > 0
        ? DeliveryService.ComputeLineTotal(Quantity.Value, UnitPrice.Value)
        : null;

    public string StockText => Product == null ? "" : Product.StockBalance.ToString("N2");

    public string Hint => Product?.Kind switch
    {
        ProductKind.VirtualCredit => "Montant de crédit (DA) × coefficient de vente",
        ProductKind.Physical => "Quantité × prix unitaire",
        _ => "",
    };

    partial void OnProductChanged(Product? value)
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(StockText));
    }

    partial void OnQuantityTextChanged(string value) => OnPropertyChanged(nameof(Total));

    partial void OnUnitPriceTextChanged(string value)
    {
        if (!_settingAutoPrice) IsPriceAutoFilled = false; // saisi à la main : on n'y touche plus
        OnPropertyChanged(nameof(Total));
    }
}

public record DeliveryRow(DeliveryNote Note)
{
    public string Number => Note.Number;
    public DateTime Date => Note.Date;
    public string ClientName => Note.Client?.Name ?? "";
    public bool IsPaymentOnly => Note.Lines.Count == 0;
    public decimal Remaining => Note.Remaining;
}

/// <summary>Ligne du détail d'un bon : un produit livré, ou le montant encaissé (<see cref="IsPayment"/>).</summary>
public record DeliveryLineRow(string ProductName, decimal? Quantity, decimal? UnitPrice, decimal LineTotal,
    string? RecipientPhone, bool IsPayment = false, UssdSendStatus? UssdStatus = null, string? UssdMessage = null)
{
    public string CreditStatusLabel => UssdStatus switch
    {
        UssdSendStatus.Sent => "Envoyé",
        UssdSendStatus.Failed => "Échec",
        _ => "",
    };

    public static DeliveryLineRow From(DeliveryLine line) => new(line.Product?.Name ?? "", line.Quantity, line.UnitPrice,
        line.LineTotal, line.RecipientPhone, UssdStatus: line.UssdStatus, UssdMessage: line.UssdMessage);

    public static DeliveryLineRow Payment(decimal amount) => new("Encaissement", null, null, amount, null, IsPayment: true);
}

public partial class DeliveriesViewModel : ViewModelBase
{
    /// <summary>Nombre maximal de clients proposés dans la liste déroulante (on affine avec la recherche).</summary>
    private const int MaxClientChoices = 100;

    private readonly DeliveryService _deliveries;
    private readonly ClientService _clients;
    private readonly ProductService _products;
    private readonly CreditTransferService _creditTransfer;
    private readonly VoiceCommandService _voice;
    private List<Client> _allClients = new();
    private List<DeliveryNote> _allNotes = new();
    private Dictionary<int, decimal> _lastPrices = new();
    /// <summary>Puce utilisée sur le dernier bon du client, par produit.</summary>
    private Dictionary<int, string> _lastRecipients = new();
    private bool _rebuildingChoices;

    public DeliveriesViewModel(DeliveryService deliveries, ClientService clients, ProductService products,
        CreditTransferService creditTransfer, VoiceCommandService voice)
    {
        _deliveries = deliveries;
        _clients = clients;
        _products = products;
        _creditTransfer = creditTransfer;
        _voice = voice;
        Lines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (DeliveryLineEditor l in e.NewItems)
                {
                    l.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(DeliveryLineEditor.Product))
                        {
                            PrefillPrice(l);
                            PopulateChips(l);
                        }
                        RefreshTotals();
                    };
                }
            RefreshTotals();
        };
        AddLine();
        _ = InitializeAsync();
    }

    public ObservableCollection<DeliveryRow> History { get; } = new();
    public ObservableCollection<DeliveryLineRow> SelectedDetails { get; } = new();
    public ObservableCollection<Client> ClientChoices { get; } = new();
    public ObservableCollection<Product> Products { get; } = new();
    public ObservableCollection<DeliveryLineEditor> Lines { get; } = new();

    [ObservableProperty] private string _historySearch = "";
    [ObservableProperty] private DateTime? _historyFrom;
    [ObservableProperty] private DateTime? _historyTo;
    [ObservableProperty] private string _historyAmountMin = "";
    [ObservableProperty] private string _historyAmountMax = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand), nameof(RetryCreditCommand))]
    private DeliveryRow? _selectedDelivery;

    /// <summary>Bon en cours de modification dans le formulaire, ou null pour un nouveau bon.</summary>
    private DeliveryNote? _editingNote;

    public string FormTitle => _editingNote == null ? "Nouveau bon" : $"Modifier le bon {_editingNote.Number}";
    public string SaveLabel => _editingNote == null ? "Enregistrer le bon" : "Enregistrer les modifications";
    public bool IsEditing => _editingNote != null;

    [ObservableProperty] private string _clientSearch = "";
    [ObservableProperty] private Client? _client;
    [ObservableProperty] private DeliveryLineEditor? _selectedLine;
    [ObservableProperty] private string _paidText = "";
    [ObservableProperty] private decimal _clientDebt;

    public decimal Total => Lines.Sum(l => l.Total ?? 0m);
    public decimal Remaining => Total - (ParseDecimal(PaidText) ?? 0m);
    /// <summary>Dette du client après ce bon. En modification, l'ancienne version du bon est d'abord retirée de sa dette.</summary>
    public decimal NewDebt => ClientDebt - (_editingNote != null && _editingNote.ClientId == Client?.Id ? _editingNote.Remaining : 0m) + Remaining;
    public bool OverLimit => Client is { CreditLimit: > 0 } c && NewDebt > c.CreditLimit;

    public string ClientChoicesHint => _allClients.Count > MaxClientChoices && ClientSearch.Trim().Length == 0
        ? $"{_allClients.Count} clients : tapez un nom ou une ville pour filtrer."
        : "";

    public string ClientInfo
    {
        get
        {
            if (Client == null) return "";
            var limit = Client.CreditLimit > 0 ? $"{Client.CreditLimit:N2} DA" : "aucun";
            var chips = string.Join(" · ", Client.Chips
                .OrderBy(c => c.OperatorId).ThenBy(c => c.Slot)
                .GroupBy(c => c.OperatorId)
                .Select(g => $"{g.First().Operator?.Name ?? "Op." + g.Key} " + string.Join(" / ", g.Select(c => c.PhoneNumber))));
            return $"Dette actuelle : {ClientDebt:N2} DA · Plafond : {limit}" + (chips.Length > 0 ? $"\nPuces : {chips}" : "\nAucune puce enregistrée");
        }
    }

    partial void OnPaidTextChanged(string value) => RefreshTotals();
    partial void OnClientSearchChanged(string value) => RebuildClientChoices();
    partial void OnHistorySearchChanged(string value) => ApplyHistoryFilter();
    partial void OnHistoryFromChanged(DateTime? value) => ApplyHistoryFilter();
    partial void OnHistoryToChanged(DateTime? value) => ApplyHistoryFilter();
    partial void OnHistoryAmountMinChanged(string value) => ApplyHistoryFilter();
    partial void OnHistoryAmountMaxChanged(string value) => ApplyHistoryFilter();

    partial void OnClientChanged(Client? value)
    {
        if (_rebuildingChoices) return; // la ComboBox vide sa sélection le temps de la reconstruction
        OnPropertyChanged(nameof(ClientInfo));
        foreach (var line in Lines) PopulateChips(line);
        _ = LoadClientContextAsync(value);
    }

    partial void OnSelectedDeliveryChanged(DeliveryRow? value)
    {
        SelectedDetails.Clear();
        if (value != null)
        {
            foreach (var l in value.Note.Lines) SelectedDetails.Add(DeliveryLineRow.From(l));
            if (value.Note.AmountPaid != 0) SelectedDetails.Add(DeliveryLineRow.Payment(value.Note.AmountPaid));
        }
        OnPropertyChanged(nameof(HasFailedCredit));
    }

    /// <summary>Le bon sélectionné a au moins une ligne de crédit virtuel dont l'envoi USSD a échoué.</summary>
    public bool HasFailedCredit => SelectedDetails.Any(l => l.UssdStatus == UssdSendStatus.Failed);

    private bool CanRetryCredit() => SelectedDelivery != null && HasFailedCredit;

    /// <summary>Retente l'envoi USSD des lignes en échec du bon sélectionné (celles déjà envoyées ne sont pas renvoyées).</summary>
    [RelayCommand(CanExecute = nameof(CanRetryCredit))]
    private async Task RetryCreditAsync()
    {
        if (SelectedDelivery == null) return;
        var noteId = SelectedDelivery.Note.Id;
        List<CreditTransferService.LineResult>? results = null;
        if (!await TryAsync(async () => results = await _creditTransfer.SendPendingAsync(noteId))) return;

        await TryAsync(ReloadProductsAndHistoryAsync);
        SelectedDelivery = History.FirstOrDefault(r => r.Note.Id == noteId);
        Info(CreditSummary(results!));
    }

    /// <summary>Message récapitulatif d'une tentative d'envoi de crédit, pour informer l'utilisateur du résultat de chaque ligne.</summary>
    private static string CreditSummary(List<CreditTransferService.LineResult> results)
    {
        if (results.Count == 0) return "Aucun crédit à envoyer sur ce bon.";
        var lines = results.Select(r => $"{(r.Success ? "✓" : "✗")} {r.ProductName} → {r.RecipientPhone} : {r.Message}");
        return "Envoi du crédit :\n" + string.Join("\n", lines);
    }

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(NewDebt));
        OnPropertyChanged(nameof(OverLimit));
    }

    private async Task LoadClientContextAsync(Client? client)
    {
        if (client == null)
        {
            ClientDebt = 0;
            _lastPrices = new();
            _lastRecipients = new();
            foreach (var line in Lines) PrefillPrice(line);
            OnPropertyChanged(nameof(ClientInfo));
            RefreshTotals();
            return;
        }

        await TryAsync(async () =>
        {
            var debt = await _deliveries.GetClientDebtAsync(client.Id);
            var prices = await _deliveries.GetLastPricesAsync(client.Id);
            var recipients = await _deliveries.GetLastRecipientsAsync(client.Id);
            if (Client?.Id != client.Id) return; // l'utilisateur a changé de client entre-temps
            ClientDebt = debt;
            _lastPrices = prices;
            _lastRecipients = recipients;
            foreach (var line in Lines)
            {
                PrefillPrice(line);
                PopulateChips(line);
            }
            OnPropertyChanged(nameof(ClientInfo));
            RefreshTotals();
        });
    }

    /// <summary>
    /// Propose le dernier coefficient de ce client pour ce produit. Un coefficient saisi à la main n'est jamais remplacé ;
    /// un coefficient proposé automatiquement est remplacé si le client ou le produit change.
    /// </summary>
    private void PrefillPrice(DeliveryLineEditor line)
    {
        if (!string.IsNullOrWhiteSpace(line.UnitPriceText) && !line.IsPriceAutoFilled) return;
        line.SetAutoPrice(line.Product != null && _lastPrices.TryGetValue(line.Product.Id, out var price)
            ? price.ToString("0.####")
            : "");
    }

    /// <summary>
    /// Propose les puces du client pour l'opérateur du produit (Flexy = Djezzy, Storm = Ooredoo, Erselli = Mobilis) ;
    /// aucune pour un produit physique, toutes pour un crédit virtuel d'un autre nom. Garde la puce déjà choisie si elle
    /// est encore valable ; sinon présélectionne celle utilisée la dernière fois pour ce produit, ou le numéro principal.
    /// </summary>
    private void PopulateChips(DeliveryLineEditor line)
    {
        var keep = line.SelectedChip?.PhoneNumber;
        line.AvailableChips.Clear();
        if (Client != null && line.Product is { Kind: ProductKind.VirtualCredit } product)
        {
            var operatorName = ProductOperators.OperatorNameFor(product.Name);
            foreach (var chip in Client.Chips
                         .Where(c => operatorName == null || string.Equals(c.Operator?.Name, operatorName, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(c => c.Operator?.Name).ThenBy(c => c.Slot))
                line.AvailableChips.Add(chip);
        }

        var lastUsed = line.Product != null && _lastRecipients.TryGetValue(line.Product.Id, out var phone) ? phone : null;
        line.SelectedChip = line.AvailableChips.FirstOrDefault(c => c.PhoneNumber == keep)
                            ?? line.AvailableChips.FirstOrDefault(c => c.PhoneNumber == lastUsed)
                            ?? line.AvailableChips.FirstOrDefault();
    }

    private async Task InitializeAsync()
    {
        await TryAsync(async () =>
        {
            _allClients = await _clients.ListAsync();
            RebuildClientChoices();
            await ReloadProductsAndHistoryAsync();
        });
    }

    private async Task ReloadProductsAndHistoryAsync()
    {
        Products.Clear();
        foreach (var p in await _products.ListAsync()) Products.Add(p);
        _allNotes = await _deliveries.ListAsync();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        var term = HistorySearch.Trim();
        var min = ParseDecimal(HistoryAmountMin);
        var max = ParseDecimal(HistoryAmountMax);

        var rows = _allNotes.Where(n => term.Length == 0
                || n.Number.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (n.Client?.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Where(n => HistoryFrom == null || n.Date.Date >= HistoryFrom.Value.Date)
            .Where(n => HistoryTo == null || n.Date.Date <= HistoryTo.Value.Date)
            .Where(n => min == null || n.Total >= min)
            .Where(n => max == null || n.Total <= max);

        History.Clear();
        foreach (var n in rows) History.Add(new DeliveryRow(n));
    }

    private void RebuildClientChoices()
    {
        var term = ClientSearch.Trim();
        var matches = _allClients
            .Where(c => term.Length == 0
                || c.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || (c.City?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Take(MaxClientChoices)
            .ToList();

        // Le client déjà choisi doit rester dans la liste, sinon WPF efface la sélection.
        var selected = Client;
        if (selected != null && matches.All(c => c.Id != selected.Id)) matches.Insert(0, selected);

        _rebuildingChoices = true;
        try
        {
            ClientChoices.Clear();
            foreach (var c in matches) ClientChoices.Add(c);
            if (selected != null) Client = ClientChoices.FirstOrDefault(c => c.Id == selected.Id);
        }
        finally
        {
            _rebuildingChoices = false;
        }
        OnPropertyChanged(nameof(ClientChoicesHint));
    }

    [RelayCommand]
    private void AddLine() => Lines.Add(new DeliveryLineEditor());

    [RelayCommand]
    private void RemoveLine()
    {
        if (SelectedLine != null) Lines.Remove(SelectedLine);
        if (Lines.Count == 0) AddLine();
    }

    [ObservableProperty] private string _voiceStatus = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VoiceFillCommand))]
    private bool _isListening;

    private bool CanUseVoice() => !IsListening;

    /// <summary>
    /// Remplit le client, le produit et le montant de la première ligne vide par la voix (client puis produit puis
    /// montant chiffre par chiffre), dans un vocabulaire fermé pour la fiabilité. Ne fait que remplir le formulaire :
    /// c'est toujours l'utilisateur qui relit et clique sur Enregistrer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseVoice))]
    private async Task VoiceFillAsync()
    {
        if (!VoiceCommandService.IsFrenchAvailable)
        {
            Info("Aucune reconnaissance vocale française n'est installée sur cette machine.\n" +
                 "Paramètres Windows > Heure et langue > Voix > Ajouter une voix > Français.");
            return;
        }

        IsListening = true;
        try
        {
            VoiceStatus = "Dites le nom du client…";
            var clientResult = await _voice.ListenForChoiceAsync(_allClients.Select(c => c.Name).ToList(), TimeSpan.FromSeconds(8));
            if (!clientResult.Success) { VoiceStatus = $"Client non reconnu : {clientResult.Error}"; return; }
            var matchedClient = _allClients.First(c => string.Equals(c.Name, clientResult.Value, StringComparison.OrdinalIgnoreCase));
            ClientSearch = matchedClient.Name;
            Client = matchedClient;

            VoiceStatus = $"Client : {matchedClient.Name}. Dites le nom du produit…";
            var productResult = await _voice.ListenForChoiceAsync(Products.Select(p => p.Name).ToList(), TimeSpan.FromSeconds(8));
            if (!productResult.Success) { VoiceStatus = $"Produit non reconnu : {productResult.Error}"; return; }
            var matchedProduct = Products.First(p => string.Equals(p.Name, productResult.Value, StringComparison.OrdinalIgnoreCase));
            var line = Lines.FirstOrDefault(l => l.Product == null) ?? Lines[0];
            line.Product = matchedProduct;

            VoiceStatus = $"Produit : {matchedProduct.Name}. Dites le montant, chiffre par chiffre…";
            var amountResult = await _voice.ListenForDigitsAsync(TimeSpan.FromSeconds(15));
            if (!amountResult.Success) { VoiceStatus = $"Montant non reconnu : {amountResult.Error}"; return; }
            line.QuantityText = amountResult.Value!;

            VoiceStatus = $"Bon rempli : {matchedClient.Name}, {matchedProduct.Name}, {amountResult.Value} DA. Vérifiez puis cliquez sur Enregistrer.";
        }
        finally
        {
            IsListening = false;
        }
    }

    private void ResetForm()
    {
        SetEditingNote(null);
        Client = null;
        ClientSearch = "";
        PaidText = "";
        VoiceStatus = "";
        Lines.Clear();
        AddLine();
    }

    private void SetEditingNote(DeliveryNote? note)
    {
        _editingNote = note;
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(IsEditing));
        RefreshTotals();
    }

    private bool HasDraft => Client != null || !string.IsNullOrWhiteSpace(PaidText) || Lines.Any(l => l.Product != null
        || !string.IsNullOrWhiteSpace(l.QuantityText) || !string.IsNullOrWhiteSpace(l.UnitPriceText));

    [RelayCommand]
    private void Reset() => ResetForm();

    /// <summary>Formulaire du bon affiché par-dessus la liste. Le fermer sans enregistrer garde la saisie en cours.</summary>
    [ObservableProperty] private bool _isFormOpen;

    [RelayCommand]
    private void OpenForm()
    {
        if (_editingNote != null) ResetForm(); // on quitte la modification d'un bon pour en créer un nouveau
        IsFormOpen = true;
    }

    [RelayCommand] private void CloseForm() => IsFormOpen = false;

    private bool HasSelection() => SelectedDelivery != null;

    /// <summary>Charge le bon sélectionné dans le formulaire pour le modifier (il garde son numéro et sa date).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (SelectedDelivery == null) return;
        var note = SelectedDelivery.Note;
        if (_editingNote == null && HasDraft
            && !Confirm("Un nouveau bon est en cours de saisie. L'abandonner pour modifier le bon sélectionné ?")) return;

        var inactive = note.Lines.FirstOrDefault(l => Products.All(p => p.Id != l.ProductId));
        if (inactive != null)
        {
            Info($"Le produit « {inactive.Product?.Name} » est désactivé : réactivez-le pour pouvoir modifier ce bon.");
            return;
        }

        ResetForm();
        SetEditingNote(note);
        Client = _allClients.FirstOrDefault(c => c.Id == note.ClientId);
        RebuildClientChoices();
        Lines.Clear();
        foreach (var l in note.Lines)
        {
            var editor = new DeliveryLineEditor();
            Lines.Add(editor);
            editor.Product = Products.First(p => p.Id == l.ProductId);
            editor.QuantityText = l.Quantity.ToString("0.##");
            editor.UnitPriceText = l.UnitPrice.ToString("0.####");
            editor.SelectedChip = editor.AvailableChips.FirstOrDefault(ch => ch.PhoneNumber == l.RecipientPhone) ?? editor.SelectedChip;
        }
        if (Lines.Count == 0) AddLine();
        PaidText = note.AmountPaid != 0 ? note.AmountPaid.ToString("0.##") : "";
        IsFormOpen = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedDelivery == null) return;
        var note = SelectedDelivery.Note;
        var isPaymentOnly = note.Lines.Count == 0;
        var question = isPaymentOnly
            ? $"Supprimer le bon d'encaissement {note.Number} ({note.AmountPaid:N2} DA) de « {note.Client?.Name} » ?\n\n" +
              "La dette du client sera augmentée de ce montant."
            : $"Supprimer le bon de livraison {note.Number} ({note.Total:N2} DA) de « {note.Client?.Name} » ?\n\n" +
              "Les produits seront remis en stock et la dette du client corrigée.";
        if (!Confirm(question + "\nCette action est définitive.")) return;

        if (!await TryAsync(() => _deliveries.DeleteAsync(note.Id))) return;
        if (_editingNote?.Id == note.Id)
        {
            ResetForm();
            IsFormOpen = false;
        }
        await TryAsync(ReloadProductsAndHistoryAsync);
        Info($"Bon {note.Number} supprimé.");
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(print: false);

    [RelayCommand]
    private Task SaveAndPrintAsync() => SaveCoreAsync(print: true);

    /// <summary>Imprime le bon en cours de modification tel qu'il est enregistré (sans les changements non enregistrés).</summary>
    [RelayCommand]
    private async Task PrintEditingAsync()
    {
        if (_editingNote != null) await PrintNoteAsync(_editingNote.Id);
    }

    private async Task PrintNoteAsync(int noteId)
    {
        DeliveryNote? note = null;
        decimal debtAfter = 0;
        if (!await TryAsync(async () =>
            {
                note = await _deliveries.GetAsync(noteId);
                if (note != null) debtAfter = await _deliveries.GetClientDebtAsync(note.ClientId);
            }) || note == null) return;

        var client = note.Client;
        var isPaymentOnly = note.Lines.Count == 0;
        // Dette du client juste avant ce bon, pour l'afficher à côté : la dette totale actuelle l'inclut déjà.
        var debtBefore = debtAfter - note.Remaining;
        const string resteLabel = "Reste (dette totale du client)";

        PrintHelper.PrintBon(isPaymentOnly ? "Bon d'encaissement" : "Bon de livraison", note.Number, note.Date,
            [("Client", client?.Name ?? ""), ("Adresse", string.Join(", ", new[] { client?.Address, client?.City }.Where(s => !string.IsNullOrWhiteSpace(s)))),
             ("Téléphone", client?.Phone ?? ""), ("Ancien solde", $"{debtBefore:N2} DA")],
            ["Produit", "Montant / qté", "Coef. / prix", "Puce", "Facturé (DA)"],
            note.Lines.Select(l => new[]
            {
                l.Product?.Name ?? "", l.Quantity.ToString("N2"), l.UnitPrice.ToString("0.####"), l.RecipientPhone ?? "", l.LineTotal.ToString("N2"),
            }).ToList(),
            [("Montant encaissé", $"{note.AmountPaid:N2} DA"), (resteLabel, $"{debtAfter:N2} DA")],
            emphasizedLabel: resteLabel);
    }

    private async Task SaveCoreAsync(bool print)
    {
        if (Client == null) { Info("Sélectionnez un client."); return; }

        var paid = string.IsNullOrWhiteSpace(PaidText) ? 0m : ParseDecimal(PaidText);
        if (paid == null) { Info("Le montant encaissé n'est pas un nombre valide."); return; }

        // Une ligne à laquelle l'utilisateur n'a rien touché est ignorée ; s'il n'en reste aucune, c'est un bon
        // d'encaissement (aucun produit vendu, seulement le montant encaissé).
        var filledLines = Lines.Where(l => l.Product != null
            || !string.IsNullOrWhiteSpace(l.QuantityText) || !string.IsNullOrWhiteSpace(l.UnitPriceText)).ToList();

        var lines = new List<DeliveryLineInput>();
        if (filledLines.Count == 0)
        {
            if (paid == 0m)
            {
                Info("Ajoutez un produit à vendre, ou saisissez un montant pour un simple encaissement (positif) ou un retrait (négatif).");
                return;
            }
        }
        else
        {
            foreach (var (line, index) in filledLines.Select((l, i) => (l, i + 1)))
            {
                if (line.Product == null) { Info($"Ligne {index} : choisissez un produit."); return; }
                if (line.Quantity is not > 0) { Info($"Ligne {index} : le montant ou la quantité n'est pas valide."); return; }
                if (line.UnitPrice is not > 0) { Info($"Ligne {index} : le coefficient ou prix n'est pas valide."); return; }
                lines.Add(new DeliveryLineInput(line.Product.Id, line.Quantity.Value, line.UnitPrice.Value, line.SelectedChip?.PhoneNumber));
            }
        }

        var editing = _editingNote;
        DeliveryNote? saved = null;
        var ok = await TryAsync(async () => saved = editing == null
            ? await _deliveries.CreateAsync(new DeliveryInput(Client.Id, DateTime.Now, lines, paid.Value))
            : await _deliveries.UpdateAsync(editing.Id, new DeliveryInput(Client.Id, editing.Date, lines, paid.Value)));
        if (!ok || saved == null) return;

        // Le crédit n'est envoyé qu'à la création (pas à la modification), pour ne jamais le transférer deux fois.
        List<CreditTransferService.LineResult>? creditResults = null;
        if (editing == null)
            await TryAsync(async () => creditResults = await _creditTransfer.SendPendingAsync(saved.Id));

        var clientName = Client.Name;
        ResetForm();
        IsFormOpen = false;
        await TryAsync(ReloadProductsAndHistoryAsync);
        SelectedDelivery = History.FirstOrDefault(r => r.Note.Id == saved.Id);

        if (editing != null)
            Info($"Bon {saved.Number} modifié pour « {clientName} » ({saved.Total:N2} DA, encaissé {saved.AmountPaid:N2} DA).\n" +
                 "Le stock, la dette du client et le rapport ont été mis à jour.");
        else
        {
            var message = lines.Count == 0
                ? $"Bon d'encaissement {saved.Number} enregistré pour « {clientName} » ({saved.AmountPaid:N2} DA)."
                : $"Bon de livraison {saved.Number} enregistré pour « {clientName} » ({saved.Total:N2} DA, reste à payer {saved.Remaining:N2} DA).\nLe stock a été mis à jour.";
            if (creditResults is { Count: > 0 }) message += "\n\n" + CreditSummary(creditResults);
            Info(message);
        }
        if (print) await PrintNoteAsync(saved.Id);
    }

    [RelayCommand]
    private void Print()
    {
        PrintHelper.PrintTable("Bons de livraison et encaissements",
            ["N°", "Date", "Client", "Total (DA)"],
            History.Select(r => new[] { r.Number, r.Date.ToString("dd/MM/yyyy"), r.ClientName, r.Remaining.ToString("N2") }).ToList());
    }
}
