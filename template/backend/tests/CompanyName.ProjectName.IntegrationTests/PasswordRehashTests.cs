#if (LocalIdentity)
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Leistd.Security.OneTimeCodes;
using CompanyName.ProjectName.Domain.Users.Entities;
using CompanyName.ProjectName.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CompanyName.ProjectName.IntegrationTests;

public sealed class PasswordRehashTests(ProjectWebApplicationFactory factory) : IClassFixture<ProjectWebApplicationFactory>
{
    private const string Password = "PasswordRehashTests!Pw";
    private const string RecoveryCode = "abcdefgh23456789";

    [Theory]
    [InlineData("allowed", true)]
    [InlineData("two-factor", true)]
    [InlineData("disabled", false)]
    [InlineData("locked", false)]
    [InlineData("wrong-password", false)]
    public async Task Only_an_allowed_correct_password_is_rehashed_and_the_security_version_survives(
        string state, bool expectedUpgrade)
    {
        var native = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions { IterationCount = 100 }));
        var oldHash = native.HashPassword(new object(), Password);
        var username = $"rehash_{Guid.NewGuid():N}"[..30];
        var user = new User(username, $"{username}@example.test", oldHash);
        if (state == "disabled") user.Disable();
        if (state == "locked") user.Lock();
        if (state == "two-factor") user.EnableTwoFactor("unused-for-recovery", [RecoveryCodes.Hash(RecoveryCode)], 0);
        var stamp = user.SecurityStamp;

        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateProjectClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/session-login", new
        {
            UsernameOrEmail = username,
            Password = state == "wrong-password" ? "wrong-password" : Password
        });
        Assert.Equal(expectedUpgrade, response.IsSuccessStatusCode);

        await using (var check = factory.Services.CreateAsyncScope())
        {
            var db = check.ServiceProvider.GetRequiredService<MyProjectDbContext>();
            var stored = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
            Assert.Equal(stamp, stored.SecurityStamp);
            Assert.Equal(expectedUpgrade, stored.PasswordHash != oldHash);
            var current = check.ServiceProvider.GetRequiredService<IPasswordHasher<object>>();
            Assert.Equal(expectedUpgrade ? PasswordVerificationResult.Success : PasswordVerificationResult.SuccessRehashNeeded,
                current.VerifyHashedPassword(new object(), stored.PasswordHash!, Password));
        }

        if (state == "two-factor")
        {
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("requiresTwoFactor").GetBoolean());
            using var completion = await client.PostAsJsonAsync("/api/v1/auth/two-factor", new
            {
                Token = body.GetProperty("twoFactorToken").GetString(),
                RecoveryCode
            });
            Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        }
    }
}
#endif
