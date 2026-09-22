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

/// <summary>Numéro de puce du client pour un opérateur donné (destinataire du crédit).</summary>
public class ClientChip
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }
    public int OperatorId { get; set; }
    public Operator? Operator { get; set; }
    public string PhoneNumber { get; set; } = "";
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ProductKind Kind { get; set; }
    /// <summary>Opérateur associé (crédit virtuel uniquement).</summary>
    public int? OperatorId { get; set; }
    public Operator? Operator { get; set; }
    /// <summary>Solde courant : montant en DA (crédit virtuel) ou quantité (produit physique). Toujours égal à la somme des mouvements.</summary>
    public decimal StockBalance { get; set; }
    public bool IsActive { get; set; } = true;

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
}

/// <summary>Paiement encaissé sur la dette d'un client (hors paiement fait à la livraison).</summary>
public class ClientPayment
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}
