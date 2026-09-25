using System.Security.Claims;
using KernelMK.Core.Entities;
using KernelMK.Web.Components.Pages.Explorer;
using KernelMK.Web.Components.Shared;

namespace KernelMK.Tests.Security;

public sealed class ComponentInputSecurityTests
{
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(@"..\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\temp\file.txt")]
    [InlineData("report.csv:secret")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("archive.")]
    [InlineData("archive ")]
    [InlineData("bad\nname")]
    [InlineData("bad\0name")]
    [InlineData("")]
    [InlineData("   ")]
    public void RemoteEntryNamesCannotEscapeTheirDirectory(string name) =>
        Assert.False(RemoteEntryName.IsValid(name));

    [Theory]
    [InlineData("factures-2026.edi")]
    [InlineData("Rapport final.csv")]
    [InlineData("échanges")]
    [InlineData(".archive")]
    public void RemoteEntryNamesAllowNormalSingleSegments(string name) =>
        Assert.True(RemoteEntryName.IsValid(name));

    [Fact]
    public void RemoteEntryNamesRejectOverlongNames() =>
        Assert.False(RemoteEntryName.IsValid(new string('a', 256)));

    [Theory]
    [InlineData(null, "Developpeur", true)]
    [InlineData("", "Developpeur", true)]
    [InlineData("Administrateur", "Developpeur", false)]
    [InlineData("Superviseur, Developpeur ", "Developpeur", true)]
    [InlineData("Superviseur", "Administrateur", true)]
    [InlineData(" , ", "Developpeur", false)]
    public void CredentialRestrictionsAreEnforced(string? allowedRoles, string role, bool expected)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        Assert.Equal(expected, CredentialAccess.CanUse(user, new Credential { AllowedRolesCsv = allowedRoles }));
    }

    [Fact]
    public void AnonymousUsersCannotUseAnUnrestrictedCredential()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(CredentialAccess.CanUse(user, new Credential()));
    }
}
