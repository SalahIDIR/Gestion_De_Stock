using GestionStock.Core.Services;

namespace GestionStock.Tests;

public class AuthTests
{
    [Fact]
    public void Hasher_accepts_correct_password_and_rejects_others()
    {
        var hash = PasswordHasher.Hash("secret123");
        Assert.True(PasswordHasher.Verify("secret123", hash));
        Assert.False(PasswordHasher.Verify("secret124", hash));
        Assert.False(PasswordHasher.Verify("secret123", "not-a-hash"));
    }

    [Fact]
    public void Hasher_uses_a_different_salt_each_time()
        => Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));

    [Fact]
    public async Task Admin_can_be_created_once_and_can_log_in()
    {
        using var db = await TestDb.CreateAsync();
        var auth = new AuthService(db);

        Assert.False(await auth.HasUsersAsync());
        await auth.CreateAdminAsync("admin", "secret123");
        Assert.True(await auth.HasUsersAsync());

        Assert.NotNull(await auth.LoginAsync("admin", "secret123"));
        Assert.Null(await auth.LoginAsync("admin", "wrong"));
        Assert.Null(await auth.LoginAsync("nobody", "secret123"));
        await Assert.ThrowsAsync<BusinessException>(() => auth.CreateAdminAsync("other", "secret123"));
    }

    [Fact]
    public async Task Short_password_is_rejected()
    {
        using var db = await TestDb.CreateAsync();
        await Assert.ThrowsAsync<BusinessException>(() => new AuthService(db).CreateAdminAsync("admin", "123"));
    }

    [Fact]
    public async Task Changing_password_requires_the_current_one()
    {
        using var db = await TestDb.CreateAsync();
        var auth = new AuthService(db);
        var user = await auth.CreateAdminAsync("admin", "secret123");

        await Assert.ThrowsAsync<BusinessException>(() => auth.ChangePasswordAsync(user.Id, "bad", "newsecret1"));
        await auth.ChangePasswordAsync(user.Id, "secret123", "newsecret1");

        Assert.Null(await auth.LoginAsync("admin", "secret123"));
        Assert.NotNull(await auth.LoginAsync("admin", "newsecret1"));
    }
}
