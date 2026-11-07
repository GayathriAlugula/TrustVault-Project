using Microsoft.AspNetCore.Identity;

namespace TrustVault.Api.Models;

public class AppUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
}