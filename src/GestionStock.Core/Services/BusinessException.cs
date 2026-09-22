namespace GestionStock.Core.Services;

/// <summary>Erreur métier dont le message (en français) peut être affiché tel quel à l'utilisateur.</summary>
public class BusinessException : Exception
{
    public BusinessException(string message) : base(message) { }
}
