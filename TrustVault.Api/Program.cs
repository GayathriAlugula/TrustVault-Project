
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Identity;
using TrustVault.Api.Data;
using TrustVault.Api.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("DefaultConnection")
    )
);

builder.Services.AddIdentityCore<AppUser>()
    .AddRoles<IdentityRole>()
    .AddSignInManager()
    .AddEntityFrameworkStores<AppDbContext>();

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT key is missing.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Convert.FromBase64String(jwtKey)
            ),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// Apply migrations, create roles and assign Admin role
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    var roleManager = scope.ServiceProvider
        .GetRequiredService<RoleManager<IdentityRole>>();

    var userManager = scope.ServiceProvider
        .GetRequiredService<UserManager<AppUser>>();

    string[] roles = { "Admin", "Owner", "Viewer" };

    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            var roleResult = await roleManager.CreateAsync(
                new IdentityRole(role)
            );

            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException(
                    string.Join(", ", roleResult.Errors.Select(e => e.Description))
                );
            }
        }
    }

    var adminEmail = builder.Configuration["Admin:Email"]?.Trim();

    Console.WriteLine("ADMIN DEBUG: Config present = " +
        !string.IsNullOrWhiteSpace(adminEmail));

    if (!string.IsNullOrWhiteSpace(adminEmail))
    {
        var adminUser = await userManager.FindByEmailAsync(adminEmail);

        Console.WriteLine("ADMIN DEBUG: User found = " +
            (adminUser != null));

        if (adminUser != null)
        {
            var currentRoles = await userManager.GetRolesAsync(adminUser);

            Console.WriteLine("ADMIN DEBUG: Current roles = " +
                string.Join(", ", currentRoles));

            if (!currentRoles.Contains("Admin"))
            {
                var result = await userManager.AddToRoleAsync(
                    adminUser,
                    "Admin"
                );

                Console.WriteLine("ADMIN DEBUG: Assignment successful = " +
                    result.Succeeded);

                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        "Admin assignment failed: " +
                        string.Join(", ", result.Errors.Select(e => e.Description))
                    );
                }
            }
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/health", () =>
    Results.Ok(new
    {
        application = "TrustVault",
        status = "Running",
        message = "TrustVault backend is working!"
    })
);

app.Run();
