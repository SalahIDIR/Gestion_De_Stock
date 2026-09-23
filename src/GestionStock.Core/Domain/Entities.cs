namespace GestionStock.Core.Domain;

public enum ProductKind
{
    /// <summary>Crédit virtuel (Flexy, Storm, Erselli) : acheté en montant global avec un coefficient.</summary>
    VirtualCredit,
    /// <summary>Produit physique (cartes Idoom, tickets) : quantité exacte et prix unitaire fixe.</summary>
    Physical,
}

public enum StockMovementKind
{
    Purchase,
    Sale,
    Adjustment,
}

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

/// <summary>Paramètres généraux de l'entreprise (une seule ligne, Id = 1).</summary>
public class AppSettings
{
    public int Id { get; set; } = 1;
    public string CompanyEmail { get; set; } = "";
    public DateTime? InventoryStart { get; set; }
    public DateTime? InventoryEnd { get; set; }
}

/// <summary>Opérateur téléphonique et routage matériel (port COM + requête USSD).</summary>
public class Operator
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Couleur d'affichage dans les rapports (#RRGGBB).</summary>
    public string ColorHex { get; set; } = "#000000";
    /// <summary>Port COM du modem assigné (ex. COM32).</summary>
    public string? ComPort { get; set; }
    /// <summary>Modèle de requête USSD, ex. *760*{numero}*{montant}#.</summary>
    public string? UssdTemplate { get; set; }

    public override string ToString() => Name;
}

/// <summary>
/// Opérateur fixe de chaque crédit virtuel, reconnu par le nom du produit : Flexy = Djezzy, Storm = Ooredoo, Erselli = Mobilis.
/// Sert à proposer la bonne puce du client dans un bon de livraison.
/// </summary>
public static class ProductOperators
{
    private static readonly Dictionary<string, string> ByProduct = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Flexy"] = "Djezzy",
        ["Storm"] = "Ooredoo",
        ["Erselli"] = "Mobilis",
    };

    /// <summary>Nom de l'opérateur du produit, ou null si le produit n'est pas un des crédits connus.</summary>
    public static string? OperatorNameFor(string? productName)
        => productName != null && ByProduct.TryGetValue(productName.Trim(), out var op) ? op : null;
}

public class Supplier
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string? Address { get; set; }
    public string? Phone1 { get; set; }
    public string? Phone2 { get; set; }

    public override string ToString() => CompanyName;
}

public class Client
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    /// <summary>Plafond de dette au-delà duquel les bons de livraison sont bloqués. 0 = aucun plafond.</summary>
    public decimal CreditLimit { get; set; }
    public List<ClientChip> Chips { get; set; } = new();

    public override string ToString() => Name;
}

/// <summary>
/// Numéro de puce du client pour un opérateur donné (destinataire du crédit).
/// Un client peut avoir jusqu'à 2 puces pour le même opérateur (Slot 1 et 2).
/// </summary>
public class ClientChip
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }
    public int OperatorId { get; set; }
    public Operator? Operator { get; set; }
    public string PhoneNumber { get; set; } = "";
    /// <summary>1 = numéro principal, 2 = deuxième numéro (facultatif) pour le même opérateur.</summary>
    public int Slot { get; set; } = 1;

    /// <summary>Libellé d'affichage dans les listes déroulantes (opérateur et numéro). Propriété calculée, non mappée par EF.</summary>
    public string DisplayLabel => (Operator != null ? $"{Operator.Name} · " : "") + (Slot == 1 ? PhoneNumber : $"{PhoneNumber} (2ᵉ numéro)");

    public override string ToString() => DisplayLabel;
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ProductKind Kind { get; set; }
    /// <summary>Solde courant : montant en DA (crédit virtuel) ou quantité (produit physique). Toujours égal à la somme des mouvements.</summary>
    public decimal StockBalance { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Couleur d'affichage dans le rapport des opérations (#RRGGBB).</summary>
    public string ColorHex { get; set; } = "#6B7280";

    public override string ToString() => Name;
}

/// <summary>Journal de stock : jamais modifié, on corrige par un mouvement inverse.</summary>
public class StockMovement
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>Positif = entrée, négatif = sortie.</summary>
    public decimal Quantity { get; set; }
    public StockMovementKind Kind { get; set; }
    public int? PurchaseOrderId { get; set; }
    public int? DeliveryNoteId { get; set; }
    public string? Note { get; set; }
}

public class PurchaseOrder
{
    public int Id { get; set; }
    /// <summary>Numéro lisible, ex. BA-000012.</summary>
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public List<PurchaseLine> Lines { get; set; } = new();

    public decimal Remaining => Total - AmountPaid;
}

public class PurchaseLine
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>Montant reçu en DA (crédit virtuel) ou nombre d'unités (produit physique).</summary>
    public decimal Quantity { get; set; }
    /// <summary>Coefficient d'achat (crédit virtuel, ex. 0.9725) ou prix unitaire (produit physique).</summary>
    public decimal UnitCost { get; set; }
    /// <summary>Coût payé au fournisseur = Quantity × UnitCost, arrondi à 2 décimales.</summary>
    public decimal LineTotal { get; set; }
}

/// <summary>Bon de livraison : vente à un client. Le reste à payer s'ajoute à sa dette.</summary>
public class DeliveryNote
{
    public int Id { get; set; }
    /// <summary>Numéro lisible, ex. BL-000012.</summary>
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }
    public decimal Total { get; set; }
    /// <summary>Somme encaissée au moment de la livraison.</summary>
    public decimal AmountPaid { get; set; }
    public List<DeliveryLine> Lines { get; set; } = new();

    public decimal Remaining => Total - AmountPaid;
}

public class DeliveryLine
{
    public int Id { get; set; }
    public int DeliveryNoteId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>Montant de crédit envoyé en DA (crédit virtuel) ou nombre d'unités (produit physique).</summary>
    public decimal Quantity { get; set; }
    /// <summary>Coefficient de vente (crédit virtuel, ex. 0.98) ou prix unitaire (produit physique).</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>Montant facturé au client = Quantity × UnitPrice, arrondi à 2 décimales.</summary>
    public decimal LineTotal { get; set; }
    /// <summary>Numéro de puce ayant reçu le crédit, tel que choisi au moment de la vente (peut différer du numéro actuel du client).</summary>
    public string? RecipientPhone { get; set; }
}

/// <summary>Paiement encaissé sur la dette d'un client (hors paiement fait à la livraison). Conservé pour l'historique ; les nouveaux encaissements sont désormais des bons sans produit (DeliveryNote).</summary>
public class ClientPayment
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Tarif de vente courant d'un produit pour un client (coefficient ou prix unitaire).
/// Fixé automatiquement lors du premier bon, modifiable ensuite ; la modification ne s'applique qu'aux futurs bons,
/// les lignes déjà enregistrées gardent leur propre valeur.
/// </summary>
public class ClientProductRate
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public int ProductId { get; set; }
    public decimal Rate { get; set; }
}

/// <summary>Même principe que <see cref="ClientProductRate"/>, côté achat (tarif fournisseur par produit).</summary>
public class SupplierProductRate
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public int ProductId { get; set; }
    public decimal Rate { get; set; }
}
