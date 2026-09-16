using Microsoft.AspNetCore.Identity;

namespace KernelMK.Data.Identity;

/// <summary>
/// Traduit en français tous les messages d'erreur générés par ASP.NET Core Identity (mot de passe,
/// création de compte, rôles...). Sans cette classe, ces messages s'affichent en anglais par défaut
/// dans toute l'application (inscription, changement de mot de passe, création d'utilisateur...).
/// </summary>
public class FrenchIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => new()
    {
        Code = nameof(DefaultError),
        Description = "Une erreur inconnue s'est produite."
    };

    public override IdentityError ConcurrencyFailure() => new()
    {
        Code = nameof(ConcurrencyFailure),
        Description = "Un conflit de concurrence s'est produit — les données ont été modifiées entre-temps par ailleurs."
    };

    public override IdentityError PasswordMismatch() => new()
    {
        Code = nameof(PasswordMismatch),
        Description = "Mot de passe incorrect."
    };

    public override IdentityError InvalidToken() => new()
    {
        Code = nameof(InvalidToken),
        Description = "Jeton invalide."
    };

    public override IdentityError LoginAlreadyAssociated() => new()
    {
        Code = nameof(LoginAlreadyAssociated),
        Description = "Un utilisateur avec cette connexion existe déjà."
    };

    public override IdentityError InvalidUserName(string? userName) => new()
    {
        Code = nameof(InvalidUserName),
        Description = $"Le nom d'utilisateur « {userName} » n'est pas valide : il ne peut contenir que des lettres et des chiffres."
    };

    public override IdentityError InvalidEmail(string? email) => new()
    {
        Code = nameof(InvalidEmail),
        Description = $"L'adresse email « {email} » n'est pas valide."
    };

    public override IdentityError DuplicateUserName(string userName) => new()
    {
        Code = nameof(DuplicateUserName),
        Description = $"Le nom d'utilisateur « {userName} » est déjà utilisé."
    };

    public override IdentityError DuplicateEmail(string email) => new()
    {
        Code = nameof(DuplicateEmail),
        Description = $"L'adresse email « {email} » est déjà utilisée par un autre compte."
    };

    public override IdentityError InvalidRoleName(string? role) => new()
    {
        Code = nameof(InvalidRoleName),
        Description = $"Le nom de rôle « {role} » n'est pas valide."
    };

    public override IdentityError DuplicateRoleName(string role) => new()
    {
        Code = nameof(DuplicateRoleName),
        Description = $"Le rôle « {role} » existe déjà."
    };

    public override IdentityError UserAlreadyHasPassword() => new()
    {
        Code = nameof(UserAlreadyHasPassword),
        Description = "L'utilisateur a déjà un mot de passe défini."
    };

    public override IdentityError UserLockoutNotEnabled() => new()
    {
        Code = nameof(UserLockoutNotEnabled),
        Description = "Le verrouillage n'est pas activé pour cet utilisateur."
    };

    public override IdentityError UserAlreadyInRole(string role) => new()
    {
        Code = nameof(UserAlreadyInRole),
        Description = $"L'utilisateur possède déjà le rôle « {role} »."
    };

    public override IdentityError UserNotInRole(string role) => new()
    {
        Code = nameof(UserNotInRole),
        Description = $"L'utilisateur ne possède pas le rôle « {role} »."
    };

    public override IdentityError PasswordTooShort(int length) => new()
    {
        Code = nameof(PasswordTooShort),
        Description = $"Le mot de passe doit contenir au moins {length} caractères."
    };

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new()
    {
        Code = nameof(PasswordRequiresUniqueChars),
        Description = $"Le mot de passe doit contenir au moins {uniqueChars} caractères distincts."
    };

    public override IdentityError PasswordRequiresNonAlphanumeric() => new()
    {
        Code = nameof(PasswordRequiresNonAlphanumeric),
        Description = "Le mot de passe doit contenir au moins un caractère spécial (non alphanumérique)."
    };

    public override IdentityError PasswordRequiresDigit() => new()
    {
        Code = nameof(PasswordRequiresDigit),
        Description = "Le mot de passe doit contenir au moins un chiffre ('0'-'9')."
    };

    public override IdentityError PasswordRequiresLower() => new()
    {
        Code = nameof(PasswordRequiresLower),
        Description = "Le mot de passe doit contenir au moins une minuscule ('a'-'z')."
    };

    public override IdentityError PasswordRequiresUpper() => new()
    {
        Code = nameof(PasswordRequiresUpper),
        Description = "Le mot de passe doit contenir au moins une majuscule ('A'-'Z')."
    };

    public override IdentityError RecoveryCodeRedemptionFailed() => new()
    {
        Code = nameof(RecoveryCodeRedemptionFailed),
        Description = "Échec de l'utilisation du code de secours."
    };
}
